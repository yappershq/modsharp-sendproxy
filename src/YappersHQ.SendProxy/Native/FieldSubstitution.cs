/*
 * SendProxy for ModSharp (CS2)
 * Copyright (C) 2026 YappersHQ. All Rights Reserved.
 *
 * This file is part of SendProxy for ModSharp.
 * SendProxy is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as
 * published by the Free Software Foundation, either version 3 of the
 * License, or (at your option) any later version.
 *
 * SendProxy is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with SendProxy. If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Hooks;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Units;
using YappersHQ.SendProxy.Shared;


namespace YappersHQ.SendProxy.Native;

// Per-client per-field value substitution. WriteFieldList runs once per changed field per client on an
// engine send worker thread; it calls GetBitRange (resolves the CFieldPath of the field being copied)
// then BitCopyPrimitive (copies the field's pre-encoded bits). Substitution rides BitCopyPrimitive: save
// the dst bit cursor, call the original, rewind, then re-emit the substitute value through the field's
// own engine encoder. RE layout/offset details: docs/REVERSE_ENGINEERING.md §8/§9.
//
// Native memory access is guarded by IsUserPtr, never by try/catch — a bad dereference faults the
// process (AccessViolation is not a catchable managed exception), so the pointer guard is the only real
// protection. The few try blocks here wrap genuine managed throw-sites: the unmanaged-callback boundary,
// the user proxy invocation, and the one-time install-time encoder enumeration. All per-field managed
// work (building the substitute buffer) completes BEFORE the native save/rewind/emit sequence, so that
// sequence has no managed throw-site and needs no guard of its own.

// Network encoder family for a registered field. The Coord3/Normal3/CoordIntegral3/QuantizedFloat set
// requires reading the live value struct from the entity snapshot (the encoder's quant logic must see
// the real float). String re-points the value at a scratch char*; ByteArray hands the encoder a scratch
// {data,count} struct. Unsupported MUST pass through untouched — writing wrong-type bits would corrupt
// the delta for every client.
internal enum FieldType
{
    Unsupported = 0,
    Int32, UInt32, Int64, Bool, Float32, Fixed32, Fixed64,
    QAngle3, Vector3,
    Coord3, Normal3, CoordIntegral3, QuantizedFloat,
    String, ByteArray,
}

internal static unsafe class FieldSubstitution
{
    // CFieldPath* filled by GetBitRange; valid for the BitCopyPrimitive call that follows on this thread.
    // Linux only: on Windows GetBitRange is inlined so _currentFieldPath stays 0; the Windows path uses
    // _currentFieldIndex instead.
    [ThreadStatic]
    private static nint _currentFieldPath;

    // Windows only: the flattened-leaf field index captured by WindowsFieldIndexHook at the WriteFieldList
    // per-field site. -1 means "not captured" (reset after each WflShim call and on Linux always).
    [ThreadStatic]
    private static int _currentFieldIndex;

    // CFlattenedSerializer* captured by the WriteFieldList shim (rdi).
    [ThreadStatic]
    private static nint _currentSerializer;

    // Entity index from WriteDeltaEntity ctx+0x34; -1 if not captured.
    [ThreadStatic]
    private static int _currentEntityIndex;

    /// <summary>The entity index captured for the WriteDeltaEntity call in scope on THIS thread (ctx+0x34),
    ///     or -1. ForceResend reads this rather than trusting an unverified WriteFields arg as the entity
    ///     index — both run on the same send-worker thread inside WriteDeltaEntity_Internal.</summary>
    public static int CurrentEntityIndex => _currentEntityIndex;

    // To-snapshot CFrameSnapshotEntry* (ctx+0x90), data regions at +0x30 — used by the live-struct path.
    [ThreadStatic]
    private static nint _currentSnapshotPtr;

    // Field-path capture uses a MID-FUNCTION hook (not an entry detour) for cross-platform consistency:
    //   Linux  — hook GetBitRange's entry; the CFieldPath* is arg0 → ctx.rdi.
    //   Windows — GetBitRange is INLINED, so hook the equivalent site inside WriteFieldList; the CFieldPath*
    //             lives in a register there (see FieldPathRegister + the windows gamedata sig).
    // A midhook only OBSERVES registers then resumes the original code — there is no trampoline to call.
    private static IMidFuncHook? _getBitRangeMidHook;
    private static IDetourHook? _valueCopyHook;
    private static nint         _valueCopyTrampoline;
    private static IDetourHook? _wflShimHook;
    private static nint         _wflShimTrampoline;
    private static IDetourHook? _wdeEntityCaptureHook;
    private static nint         _wdeEntityCaptureTrampoline;

    private static ILogger? _logger;

    // Client manager used to resolve CServerSideClient* → IGameClient via engine slot. Set at Install.
    // The lookup is a pointer/array read — safe to call on the engine send worker thread.
    private static IClientManager? _clientManager;

    // CServerSideClient::m_Slot — engine.games.jsonc resolves this to 72 (0x48) on both linux and windows.
    // The recipient pointer captured at PerClientEncode (rsi) is a CServerSideClient*; its slot maps 1:1 to
    // an IGameClient via IClientManager.GetGameClient(PlayerSlot).
    private const int CServerSideClientSlotOffset = 0x48;

    public static nint GetBitRangeAddr;
    // Windows: address of the WriteFieldList per-field site (gamedata "CFlattenedSerializer::WriteFieldList_FieldPathSite").
    // On Linux this is zero and is never used; on Windows it replaces GetBitRangeAddr as the midhook target.
    public static nint WindowsFieldPathSiteAddr;
    public static nint ValueCopyAddr;
    public static nint WriteFieldListAddr;
    public static nint WdeAddr;

    // Encoder identities resolved from gamedata (set by SendProxyModule before Install).
    private static nint   _registryAddr;
    private static nint[] _bucketAddrs = Array.Empty<nint>();
    private static nint   _encodeInt32Addr;

    // Bucket-index parallel to _bucketAddrs entries (engine encoder-registry bucket numbers).
    private static readonly int[] BucketIndices = { 1, 2, 3, 4, 5, 6, 7 };

    // fn pointer -> FieldType, built once at Install from the gamedata-resolved bucket handler bases.
    private static Dictionary<nint, FieldType>? _encoderTypes;
    private static readonly object _encoderTypesLock = new();

    // Windows index→name cache: serializerName -> string[] indexed by flattened-leaf DFS order.
    // Built lazily from WflShim the first time each serializer is seen (Windows only; never populated
    // on Linux — _currentFieldIndex stays -1 and the Windows branch in ValueCopyHook is unreachable).
    // serializer name char* -> index→field-name-char* array (Windows index-based field-path resolution).
    private static readonly ConcurrentDictionary<nint, nint[]> _windowsIndexName = new();

    // Drop the cached serializer→index map. Called on level activation (serializer metadata can be rebuilt,
    // so the cached name char* could become stale) and on shutdown.
    public static void ClearWindowsIndex()
        => _windowsIndexName.Clear();

    /// <summary>
    ///     Provide the gamedata-resolved encoder identities used for classification: the registry table
    ///     base, the per-bucket handler array bases (parallel to <see cref="BucketIndices"/>), and the
    ///     standalone int32 encoder fn (cross-check).
    /// </summary>
    public static void SetEncoderResolution(nint registryAddr, nint[] bucketAddrs, nint encodeInt32Addr)
    {
        _registryAddr    = registryAddr;
        _bucketAddrs     = bucketAddrs ?? Array.Empty<nint>();
        _encodeInt32Addr = encodeInt32Addr;
    }

    /// <summary>
    ///     Build the encoder type map now (idempotent) so classification is ready — and diagnosable — at
    ///     load, instead of lazily on first registration. The detours still install lazily.
    /// </summary>
    public static void PrebuildEncoderMap(ILogger logger)
    {
        if (_encoderTypes is null)
        {
            lock (_encoderTypesLock)
            {
                _encoderTypes ??= BuildEncoderTypeMap(logger);
            }
        }
    }

    private static readonly Dictionary<nint, FieldType> _emptyEncoderMap = new();

    /// <summary>The fn-pointer → FieldType encoder map (empty until <see cref="PrebuildEncoderMap"/> runs).</summary>
    public static IReadOnlyDictionary<nint, FieldType> EncoderTypeMap => _encoderTypes ?? _emptyEncoderMap;

    public static bool Install(InterfaceBridge bridge, ILogger logger)
    {
        _logger        = logger;
        _clientManager = bridge.ClientManager;

        if (!ValidateAddresses(logger))
        {
            return false;
        }

        if (_encoderTypes is null)
        {
            lock (_encoderTypesLock)
            {
                _encoderTypes ??= BuildEncoderTypeMap(logger);
            }
        }

        if (_wflShimHook is null)
        {
            var wflHook = bridge.HookManager.CreateDetourHook();
            var wflHookFn = (nint) (delegate* unmanaged[Cdecl]<
                nint, nint, nint, nint, nint, uint,
                uint, nint, uint,
                nint>) &WflShim;
            wflHook.Prepare(WriteFieldListAddr, wflHookFn);

            if (!wflHook.Install())
            {
                logger.LogWarning("FieldSubstitution: WriteFieldList shim Install() failed");

                return false;
            }

            _wflShimHook       = wflHook;
            _wflShimTrampoline = wflHook.Trampoline;
            logger.LogInformation("FieldSubstitution: WriteFieldList shim installed @ 0x{Addr:X}", WriteFieldListAddr);
        }

        if (_getBitRangeMidHook is null)
        {
            var gbrHook = bridge.HookManager.CreateMidFuncHook();

            if (OperatingSystem.IsWindows())
            {
                var gbrHookFn = (nint) (delegate* unmanaged[Cdecl]<MidHookContext*, void>) &WindowsFieldIndexHook;
                gbrHook.Prepare(WindowsFieldPathSiteAddr, gbrHookFn);
            }
            else
            {
                var gbrHookFn = (nint) (delegate* unmanaged[Cdecl]<MidHookContext*, void>) &GetBitRangePathHook;
                gbrHook.Prepare(GetBitRangeAddr, gbrHookFn);
            }

            if (!gbrHook.Install())
            {
                logger.LogWarning("FieldSubstitution: field-path mid-hook Install() failed");
                DisposeHook(ref _wflShimHook, ref _wflShimTrampoline);

                return false;
            }

            _getBitRangeMidHook = gbrHook;
            if (OperatingSystem.IsWindows())
            {
                logger.LogInformation("FieldSubstitution: WriteFieldList field-index mid-hook installed @ 0x{Addr:X}", WindowsFieldPathSiteAddr);
            }
            else
            {
                logger.LogInformation("FieldSubstitution: GetBitRange mid-hook installed @ 0x{Addr:X}", GetBitRangeAddr);
            }
        }

        if (_valueCopyHook is null)
        {
            var vcHook   = bridge.HookManager.CreateDetourHook();
            var vcHookFn = (nint) (delegate* unmanaged[Cdecl]<nint, nint, uint, byte>) &ValueCopyHook;
            vcHook.Prepare(ValueCopyAddr, vcHookFn);

            if (!vcHook.Install())
            {
                logger.LogWarning("FieldSubstitution: value-copy hook Install() failed");
                DisposeMidHook(ref _getBitRangeMidHook);
                DisposeHook(ref _wflShimHook, ref _wflShimTrampoline);

                return false;
            }

            _valueCopyHook       = vcHook;
            _valueCopyTrampoline = vcHook.Trampoline;
            logger.LogInformation("FieldSubstitution: value-copy hook installed @ 0x{Addr:X}", ValueCopyAddr);
        }

        if (_wdeEntityCaptureHook is null && WdeAddr != 0)
        {
            var wdeHook   = bridge.HookManager.CreateDetourHook();
            var wdeHookFn = (nint) (delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint>) &WdeEntityCaptureHook;
            wdeHook.Prepare(WdeAddr, wdeHookFn);

            if (wdeHook.Install())
            {
                _wdeEntityCaptureHook       = wdeHook;
                _wdeEntityCaptureTrampoline = wdeHook.Trampoline;
                logger.LogInformation("FieldSubstitution: WriteDeltaEntity entity-index capture installed @ 0x{Addr:X}", WdeAddr);
            }
            else
            {
                logger.LogWarning("FieldSubstitution: WriteDeltaEntity entity-index capture Install() failed — entityIndex will be -1 in callbacks");
                wdeHook.Dispose();
            }
        }

        return true;
    }

    public static void Uninstall()
    {
        DisposeHook(ref _valueCopyHook, ref _valueCopyTrampoline);
        DisposeMidHook(ref _getBitRangeMidHook);
        DisposeHook(ref _wflShimHook, ref _wflShimTrampoline);
        DisposeHook(ref _wdeEntityCaptureHook, ref _wdeEntityCaptureTrampoline);

        _logger?.LogInformation("FieldSubstitution: all hooks uninstalled");
        _logger = null;
    }

    private static void DisposeHook(ref IDetourHook? hook, ref nint trampoline)
    {
        hook?.Uninstall();
        hook?.Dispose();
        hook       = null;
        trampoline = 0;
    }

    private static void DisposeMidHook(ref IMidFuncHook? hook)
    {
        hook?.Uninstall();
        hook?.Dispose();
        hook = null;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static nint WflShim(
        nint a,
        nint b, nint c, nint d, nint e,
        uint p6,
        uint p7, nint p8, uint p9)
    {
        // No per-client registrations → nothing to substitute below; skip the serializer capture entirely.
        if (PerClientDispatch.IsEmpty)
        {
            return ((delegate* unmanaged[Cdecl]<
                nint, nint, nint, nint, nint, uint,
                uint, nint, uint,
                nint>) _wflShimTrampoline)(a, b, c, d, e, p6, p7, p8, p9);
        }

        _currentSerializer  = a;
        _currentFieldIndex  = -1;

        // Windows: build the index→namePtr array the first time this serializer is seen so ValueCopyHook can
        // resolve a field index captured by WindowsFieldIndexHook into a field name pointer. (Linux walks the
        // CFieldPath* directly and needs no serializer-name read here.)
        if (OperatingSystem.IsWindows())
        {
            NoteSerializerWindows(*(nint*) (a + 0x00), a);
        }

        var result = ((delegate* unmanaged[Cdecl]<
            nint, nint, nint, nint, nint, uint,
            uint, nint, uint,
            nint>) _wflShimTrampoline)(a, b, c, d, e, p6, p7, p8, p9);
        _currentSerializer = 0;
        _currentFieldIndex = -1;

        return result;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static nint WdeEntityCaptureHook(nint a, nint b, nint c, nint d, nint e, nint f)
    {
        // No per-client registrations → skip the entity-index/snapshot capture.
        if (PerClientDispatch.IsEmpty)
        {
            return ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint>)
                _wdeEntityCaptureTrampoline)(a, b, c, d, e, f);
        }

        var entityIndex = -1;
        var snapshotPtr = (nint) 0;

        if (NativeUtil.IsUserPtr(b))
        {
            entityIndex = *(int*) (b + 0x34);

            var raw = *(nint*) (b + 0x90);
            if (NativeUtil.IsUserPtr(raw))
            {
                snapshotPtr = raw;
            }
        }

        _currentEntityIndex = entityIndex;
        _currentSnapshotPtr = snapshotPtr;

        var result = ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint>)
            _wdeEntityCaptureTrampoline)(a, b, c, d, e, f);

        _currentEntityIndex = -1;
        _currentSnapshotPtr = 0;

        return result;
    }

    // Mid-function hook callback: OBSERVES the registers at the hooked site, then PolyHook resumes the
    // original code (no trampoline call — that's the midhook contract). Captures the live CFieldPath* into
    // _currentFieldPath so ValueCopyHook (BitCopyPrimitive) can resolve which field is being copied.
    //   Linux  : hooked at GetBitRange entry → CFieldPath* is arg0 → ctx->rdi (SysV first integer arg).
    //   Windows: this CFieldPath-pointer model DOES NOT PORT. RE of WriteFieldList (engine2.dll) shows
    //            Windows resolves the field by BINARY-SEARCHING the field key over the serializer's sorted
    //            field array (`mov edx,[r12+rsi*4]; cmp edx,r14d` bisection) — the per-field identity is an
    //            INDEX, there is no CFieldPath struct passed/held in a register. So a Windows per-client
    //            substitution needs an INDEX→name resolution (the reverse of ForceResend's flattened-leaf
    //            cache) captured at the per-field site, not a FieldPathReg pointer. FieldPathReg below stays
    //            Linux-only; the Windows path is a separate index-based design (see docs/FORCE_RESEND.md
    //            + the leaf-index walk in ForceResend.WalkLeaves which already produces the name↔index map).
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void GetBitRangePathHook(MidHookContext* ctx)
    {
        // Only capture when there's something to substitute (ValueCopyHook is gated the same way).
        if (PerClientDispatch.IsEmpty)
        {
            return;
        }

        var pathOut = ReadFieldPathRegister(ctx);
        _currentFieldPath = NativeUtil.IsUserPtr(pathOut) ? pathOut : 0;
    }

    // Windows: mid-function hook at WriteFieldList's per-field site. R12 = current field INDEX (1:1 with
    // the BitCopy for that field). ValueCopyHook resolves this index to a name via _windowsIndexName.
    // On Linux this callback is never installed; GetBitRangePathHook is used instead.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void WindowsFieldIndexHook(MidHookContext* ctx)
    {
        if (PerClientDispatch.IsEmpty)
        {
            return;
        }

        _currentFieldIndex = (int) ctx->r12;
    }

    // Build and cache the index→name-POINTER array for a serializer the first time it is seen (Windows only),
    // keyed by the serializer's stable name pointer. Index = sequential DFS leaf order. No managed string —
    // the per-client consume path matches by name pointer. Every deref is IsUserPtr-guarded; try/catch so a
    // bad walk never escapes.
    private static void NoteSerializerWindows(nint serNamePtr, nint serializer)
    {
        if (serNamePtr == 0 || _windowsIndexName.ContainsKey(serNamePtr) || !NativeUtil.IsUserPtr(serializer))
        {
            return;
        }

        var list = new List<nint>();
        try
        {
            WalkLeavesIndexed(serializer, list, 0);
        }
        catch
        {
            return; // never let a bad walk poison the cache
        }

        _windowsIndexName.TryAdd(serNamePtr, list.ToArray());
    }

    // DFS walk producing an ordered leaf name-pointer list (index = sequential position): base
    // @serializer+0x30, stride 0x2E; child serializer @rec+0x08; leaf fieldInfo @rec+0x00; name char*
    // @*(fieldInfo+0x08). Guard: depth≤4, count≤4096.
    private static void WalkLeavesIndexed(nint serializer, List<nint> namePtrs, int depth)
    {
        if (depth > 4 || !NativeUtil.IsUserPtr(serializer))
        {
            return;
        }

        var count = *(int*) (serializer + 0x28);
        if (count <= 0 || count > 4096)
        {
            return;
        }

        var arr = *(nint*) (serializer + 0x30);
        if (!NativeUtil.IsUserPtr(arr))
        {
            return;
        }

        for (var i = 0; i < count; i++)
        {
            var rec   = arr + (nint) i * 0x2E;
            var child = *(nint*) (rec + 0x08);
            if (NativeUtil.IsUserPtr(child))
            {
                WalkLeavesIndexed(child, namePtrs, depth + 1);
                continue;
            }

            var fieldInfo = *(nint*) (rec + 0x00);
            if (!NativeUtil.IsUserPtr(fieldInfo))
            {
                namePtrs.Add(0); // placeholder to keep index alignment
                continue;
            }

            namePtrs.Add(*(nint*) (fieldInfo + 0x08));
        }
    }

    // Which MidHookContext register the field-path midhook reads at the hooked site.
    //   Linux  : GetBitRange entry → rdi (arg0) = CFieldPath* (a pointer; ResolveFieldName walks it).
    //   Windows: GetBitRange is inlined, so the midhook hooks the WriteFieldList per-field site instead
    //            (gamedata "CFlattenedSerializer::WriteFieldList_FieldPathSite"); there R12 = the field
    //            INDEX (not a pointer — Windows is index-based), so the Windows capture reads r12 as an int
    //            index and resolves it via the serializer leaf map (index→name). The full Windows install +
    //            index→name wiring is the documented next step (needs the leaf index↔name numbering verified
    //            on hardware + the delta-path BuildMergedSerializedEntity site for full coverage — see
    //            docs/FORCE_RESEND.md). Default below is the Linux pointer-reg; SetFieldPathRegister overrides.
    private static FieldPathReg _fieldPathReg =
        OperatingSystem.IsWindows() ? FieldPathReg.R12 : FieldPathReg.Rdi;

    internal enum FieldPathReg { Rdi, Rsi, Rdx, Rcx, R8, R9, R15, R14, R13, R12 }

    /// <summary>Override the register the field-path mid-hook reads (for Windows hardware tuning).</summary>
    public static void SetFieldPathRegister(string reg)
    {
        if (Enum.TryParse<FieldPathReg>(reg, ignoreCase: true, out var r))
        {
            _fieldPathReg = r;
        }
    }

    private static nint ReadFieldPathRegister(MidHookContext* ctx)
        => _fieldPathReg switch
        {
            FieldPathReg.Rdi => ctx->rdi,
            FieldPathReg.Rsi => ctx->rsi,
            FieldPathReg.Rdx => ctx->rdx,
            FieldPathReg.Rcx => ctx->rcx,
            FieldPathReg.R8  => ctx->r8,
            FieldPathReg.R9  => ctx->r9,
            FieldPathReg.R15 => ctx->r15,
            FieldPathReg.R14 => ctx->r14,
            FieldPathReg.R13 => ctx->r13,
            FieldPathReg.R12 => ctx->r12,
            _                => ctx->rdi,
        };

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static byte ValueCopyHook(nint dst, nint src, uint bitcount)
    {
        // Hot path: this fires for every field copy of every entity for every client. With no per-client
        // registrations there is nothing to substitute, so short-circuit before any resolve work — the
        // detour then costs only this one check plus the trampoline.
        if (PerClientDispatch.IsEmpty)
        {
            return CallOriginal(dst, src, bitcount);
        }

        if (!NativeUtil.IsUserPtr(_currentSerializer))
        {
            return CallOriginal(dst, src, bitcount);
        }

        var pathPtr = _currentFieldPath;

        // On Linux pathPtr is set by GetBitRangePathHook; on Windows GetBitRange is inlined so pathPtr
        // stays 0 — use the index captured by WindowsFieldIndexHook and the pre-built index→name map.
        if (!NativeUtil.IsUserPtr(pathPtr) && !(OperatingSystem.IsWindows() && _currentFieldIndex >= 0))
        {
            return CallOriginal(dst, src, bitcount);
        }

        try
        {
            var serPtr     = _currentSerializer;
            var serNamePtr = NativeUtil.IsUserPtr(serPtr) ? *(nint*) (serPtr + 0x00) : 0;

            nint fieldNamePtr;
            nint leafRec;

            if (OperatingSystem.IsWindows() && !NativeUtil.IsUserPtr(pathPtr))
            {
                // Windows index-based path: resolve the field NAME POINTER from the pre-built index→ptr array.
                leafRec = 0;
                if (serNamePtr == 0
                    || !_windowsIndexName.TryGetValue(serNamePtr, out var indexPtrs)
                    || _currentFieldIndex >= indexPtrs.Length
                    || (fieldNamePtr = indexPtrs[_currentFieldIndex]) == 0)
                {
                    return CallOriginal(dst, src, bitcount);
                }

                // Resolve leafRec for Classify/TryGetLiveValuePtr by walking to the leaf at this index.
                leafRec = ResolveLeafRecByIndex(serPtr, _currentFieldIndex);
                if (leafRec == 0)
                {
                    return CallOriginal(dst, src, bitcount);
                }
            }
            else
            {
                // Linux CFieldPath* path.
                fieldNamePtr = ResolveFieldNamePtr(serPtr, pathPtr, out leafRec);
                if (fieldNamePtr == 0)
                {
                    return CallOriginal(dst, src, bitcount);
                }
            }

            var clientPtr   = RecipientCapture.CurrentClient;
            var entityIndex = _currentEntityIndex;

            SpoofValue spoofVal;
            FieldType  fieldType;

            // -- Per-viewer proxy consume (IProxyManager SetFor) --------------------------------------
            // If a proxy recorded per-recipient values for this (entity, field) during the shared pack,
            // apply THIS client's value: its SetFor override, or the recorded default (uniform value if also
            // SetAll'd, else the real value — restoring non-recipients).
            if (!NativeUtil.IsUserPtr(clientPtr)
                || !PerClientDispatch.TryResolve(entityIndex, fieldNamePtr,
                    *(int*) (clientPtr + CServerSideClientSlotOffset), out var pcValue, out _))
            {
                return CallOriginal(dst, src, bitcount);
            }

            fieldType = Classify(leafRec);
            if (fieldType == FieldType.Unsupported)
            {
                return CallOriginal(dst, src, bitcount);
            }

            spoofVal = pcValue;

            // Extract the typed sub-values from the (possibly mutated) SpoofValue using the SAME
            // bit-conversion logic that existed before: Float32 reads float bits, UInt32 reads uint cast,
            // Bool checks != 0, vectors read XYZ, string/bytes read the ref fields. The raw-int-bits path
            // (Int32/Int64/Fixed32/Fixed64) reads _intBits directly via RawIntBits.
            var intBits = spoofVal.RawIntBits;
            var vec     = spoofVal.RawVec;
            var str     = spoofVal.RawStr;
            var bytes   = spoofVal.RawBytes;
            // Float32 is stored in RawFloat; re-interpret to int bits for the encode path (identical to
            // the original InvokeCallback Float case: BitConverter.SingleToInt32Bits(f) → intBits).
            if (spoofVal.Kind == SpoofKind.Float)
            {
                intBits = BitConverter.SingleToInt32Bits(spoofVal.RawFloat);
            }

            // Resolve the field's own engine encoder (dispatch slot 0 at fieldInfo+0x38) and its params.
            var fieldInfo = *(nint*) (leafRec + 0x00);
            var dispatch  = *(nint*) (fieldInfo + 0x38);
            var encoderFn = NativeUtil.IsUserPtr(dispatch) ? *(nint*) dispatch : 0;
            if (!NativeUtil.IsUserPtr(encoderFn))
            {
                return CallOriginal(dst, src, bitcount);
            }

            var paramOff  = *(byte*) (fieldInfo + 0xC9);
            var paramsPtr = (paramOff == 0xFF) ? 0 : (*(nint*) (fieldInfo + 0x40) + paramOff);

            // Build the substitute value pointer in the layout this encoder expects. All allocation /
            // managed work happens here, BEFORE the native save/rewind/emit below — so that sequence is
            // pure native and cannot throw a managed exception mid-rewrite.
            nint valuePtr;
            var  scratch     = stackalloc byte[LiveScratchSize];
            var  stringSlot  = stackalloc nint[1];

            switch (fieldType)
            {
                case FieldType.String:
                {
                    var s       = str ?? string.Empty;
                    var byteLen = Encoding.UTF8.GetByteCount(s);

                    // The scratch lives on the engine send worker thread's stack; cap it so a caller can't
                    // overflow that stack with an oversized value. Oversized -> pass the real value through.
                    if (byteLen > MaxSubstituteBytes)
                    {
                        return CallOriginal(dst, src, bitcount);
                    }

                    var strBuf = stackalloc byte[byteLen + 1];
                    Encoding.UTF8.GetBytes(s, new Span<byte>(strBuf, byteLen));
                    strBuf[byteLen] = 0;
                    *stringSlot     = (nint) strBuf;        // encoder reads *valuePtr as char*
                    valuePtr        = (nint) stringSlot;
                    break;
                }

                case FieldType.ByteArray:
                {
                    var b = bytes ?? Array.Empty<byte>();
                    if (b.Length > MaxSubstituteBytes)
                    {
                        return CallOriginal(dst, src, bitcount);
                    }

                    var buf = stackalloc byte[b.Length];
                    for (var i = 0; i < b.Length; i++)
                    {
                        buf[i] = b[i];
                    }

                    // Encoder reads {+0x00 data*, +0x28 uint count}; LiveScratchSize (0x30) covers it.
                    for (var i = 0; i < LiveScratchSize; i++)
                    {
                        scratch[i] = 0;
                    }

                    *(nint*) (scratch + 0x00)  = (nint) buf;
                    *(uint*) (scratch + 0x28)  = (uint) b.Length;
                    valuePtr                   = (nint) scratch;
                    break;
                }

                case FieldType.Coord3:
                case FieldType.Normal3:
                case FieldType.CoordIntegral3:
                case FieldType.QuantizedFloat:
                {
                    // The quant encoder must see the real live struct; copy it, then patch the float(s).
                    if (!TryGetLiveValuePtr(leafRec, out var liveValuePtr))
                    {
                        return CallOriginal(dst, src, bitcount);
                    }

                    for (var i = 0; i < LiveScratchSize; i++)
                    {
                        scratch[i] = *(byte*) (liveValuePtr + i);
                    }

                    if (spoofVal.Kind == SpoofKind.Vector)
                    {
                        ((float*) scratch)[0] = vec.X;
                        ((float*) scratch)[1] = vec.Y;
                        ((float*) scratch)[2] = vec.Z;
                    }
                    else
                    {
                        ((float*) scratch)[0] = BitConverter.Int32BitsToSingle(intBits);
                    }

                    valuePtr = (nint) scratch;
                    break;
                }

                case FieldType.QAngle3:
                case FieldType.Vector3:
                    ((float*) scratch)[0] = vec.X;
                    ((float*) scratch)[1] = vec.Y;
                    ((float*) scratch)[2] = vec.Z;
                    valuePtr = (nint) scratch;
                    break;

                case FieldType.Float32:
                    *(double*) scratch = BitConverter.Int32BitsToSingle(intBits);
                    valuePtr           = (nint) scratch;
                    break;

                case FieldType.UInt32:
                    *(ulong*) scratch = (uint) intBits;
                    valuePtr          = (nint) scratch;
                    break;

                case FieldType.Bool:
                    *scratch = (byte) (intBits != 0 ? 1 : 0);
                    valuePtr = (nint) scratch;
                    break;

                default: // Int32 / Int64 / Fixed32 / Fixed64
                    *(long*) scratch = intBits;
                    valuePtr         = (nint) scratch;
                    break;
            }

            // Emit via the SAME mechanism the (working) uniform path uses: the engine's own
            // BitCopyPrimitive copying fake bits into the value buffer. RE: docs/RE_PERCLIENT_EMIT.md.
            //
            //   1. encode the fake into a fresh, zeroed, byte-aligned scratch bf_write (cursor 0),
            //   2. advance the real src cursor past the real value (so following fields still read right),
            //   3. rewind scratch to bit 0 and call the ORIGINAL BitCopyPrimitive(dst, scratch, N).
            //
            // dst (the WriteFieldList intermediate value buffer) is then written by unchanged engine code,
            // identical to uniform — only the source of the fake bits differs (a per-client scratch built
            // here vs. the shared pre-encoded snapshot). All work below is pure native: no managed
            // throw-site between the scratch encode and the copy.

            // Upper bound on the encoded byte length: scalars/vectors/quantized fit in a few words; string
            // and byte-array encoders emit a length prefix + payload. Bounded by MaxSubstituteBytes (the
            // value buffer was already capped above for those families).
            var encBound = fieldType switch
            {
                FieldType.String    => Encoding.UTF8.GetByteCount(str ?? string.Empty) + 16,
                FieldType.ByteArray => (bytes?.Length ?? 0) + 16,
                _                   => 32,
            };
            if (encBound > MaxSubstituteBytes + 16)
            {
                return CallOriginal(dst, src, bitcount);
            }

            var dataBuf = stackalloc byte[encBound];
            for (var i = 0; i < encBound; i++)
            {
                dataBuf[i] = 0;
            }

            // Scratch bf_write header (verified layout): data @+0x00, nDataBytes @+0x08, nDataBits @+0x0c,
            // cursor @+0x10, overflow @+0x20, flag @+0x22. ScratchBfSize covers it with slack.
            var bw = stackalloc byte[ScratchBfSize];
            for (var i = 0; i < ScratchBfSize; i++)
            {
                bw[i] = 0;
            }

            *(nint*) (bw + 0x00) = (nint) dataBuf;
            *(int*) (bw + 0x08)  = encBound;
            *(int*) (bw + 0x0C)  = encBound * 8;
            *(int*) (bw + 0x10)  = 0;      // cursor: byte-aligned at 0 -> encoder takes its simple fast path
            *(bw + 0x20)         = 0;      // overflow
            *(bw + 0x22)         = 0;      // flag (0 = normal)

            // Encode the fake into the scratch. The field's own encoder reads valuePtr and writes its
            // wire form (varint / fixed / length-prefixed) at the scratch cursor, advancing it.
            ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, uint, void>)
                encoderFn)((nint) bw, fieldInfo, paramsPtr, valuePtr, 0u);

            var encodedBits = *(int*) (bw + 0x10);
            var overflowed  = *(bw + 0x20);
            if (overflowed != 0 || encodedBits <= 0)
            {
                // Encoder overflowed the scratch or wrote nothing — don't risk a corrupt stream.
                return CallOriginal(dst, src, bitcount);
            }

            // Skip the real value in the source, then copy our fake bits into dst via the engine's own
            // primitive (rewind scratch to read from bit 0 first).
            *(int*) (src + 0x10) += (int) bitcount;
            *(int*) (bw + 0x10) = 0;

            return CallOriginal(dst, (nint) bw, (uint) encodedBits);
        }
        catch
        {
            // Unmanaged-callback boundary: never let a managed exception escape into engine code. Reached
            // only for failures BEFORE the native emit sequence above, so a plain passthrough is correct.
            return CallOriginal(dst, src, bitcount);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte CallOriginal(nint dst, nint src, uint bitcount)
        => ((delegate* unmanaged[Cdecl]<nint, nint, uint, byte>) _valueCopyTrampoline)(dst, src, bitcount);

    // Resolve the CServerSideClient* recipient (captured at PerClientEncode) to an IGameClient via its
    // engine slot (m_Slot @ +0x48). Returns null if the manager is unset, the pointer is bad, or the slot
    // is out of range — the caller then passes the real value through.
    private static IGameClient? ResolveClient(nint serverSideClient)
    {
        if (_clientManager is not { } cm || !NativeUtil.IsUserPtr(serverSideClient))
        {
            return null;
        }

        var slot = *(int*) (serverSideClient + CServerSideClientSlotOffset);
        if (slot < 0 || slot > PlayerSlot.MaxPlayerSlot.AsPrimitive())
        {
            return null;
        }

        return cm.GetGameClient((PlayerSlot) (byte) slot);
    }

    // Walk the CFieldPath (filled by GetBitRange) through the flattened-serializer field array to the leaf
    // record + its field name. Every dereference is IsUserPtr-guarded; a guard failure returns empty (the
    // caller passes through). RE layout: docs/REVERSE_ENGINEERING.md §9b.
    private static nint ResolveFieldNamePtr(nint serializer, nint hdr, out nint leafRec)
    {
        leafRec = 0;
        if (!NativeUtil.IsUserPtr(serializer) || !NativeUtil.IsUserPtr(hdr))
        {
            return 0;
        }

        // Path levels. CFieldPath is at most 3 deep (§9a); a larger/garbage count means a stale or bogus
        // path — bail rather than walk it.
        var count = *(short*) (hdr + 0x18);
        if (count < 1 || count > 3)
        {
            return 0;
        }

        nint idxArr;
        if (*(byte*) (hdr + 0x1A) != 0)
        {
            idxArr = *(nint*) hdr;
            if (!NativeUtil.IsUserPtr(idxArr))
            {
                return 0;
            }
        }
        else
        {
            idxArr = hdr;
        }

        var serArr = serializer;
        var rec    = (nint) 0;

        for (var k = 0; k < count; k++)
        {
            var idxK = *(short*) (idxArr + k * 2);
            if (idxK == 0x7FFF)
            {
                if (k == 0)
                {
                    return 0;
                }

                break;
            }

            // For levels > 0, descend into the child serializer of the current record first.
            if (k > 0)
            {
                var child = *(nint*) (rec + 0x08);
                if (!NativeUtil.IsUserPtr(child))
                {
                    return 0;
                }

                serArr = child;
            }

            // Bound the index against the serializer's field-array length so a stale/garbage index can
            // never deref off the array (the fatal-AV path). The length is the CUtlVector size that
            // precedes the data pointer at +0x30; fall back to a hard cap if it reads implausibly.
            if (!IndexInBounds(serArr, idxK))
            {
                return 0;
            }

            var arr = *(nint*) (serArr + 0x30);
            if (!NativeUtil.IsUserPtr(arr))
            {
                return 0;
            }

            rec = arr + idxK * 0x2E;
            if (!NativeUtil.IsUserPtr(rec))
            {
                return 0;
            }
        }

        var pInfo = *(nint*) (rec + 0x00);
        if (!NativeUtil.IsUserPtr(pInfo))
        {
            return 0;
        }

        leafRec = rec;

        return *(nint*) (pInfo + 0x08);
    }

    // Windows: resolve the leafRec (record pointer) for a given flattened-leaf DFS index so Classify and
    // TryGetLiveValuePtr can inspect the field's encoder and snapshot region. Mirrors WalkLeavesIndexed but
    // stops once it reaches the target index. IsUserPtr-guarded; returns 0 on any guard failure.
    private static nint ResolveLeafRecByIndex(nint serializer, int targetIndex)
    {
        var current = 0;

        return WalkToLeafRec(serializer, ref current, targetIndex, 0);
    }

    private static nint WalkToLeafRec(nint serializer, ref int current, int target, int depth)
    {
        if (depth > 4 || !NativeUtil.IsUserPtr(serializer))
        {
            return 0;
        }

        var count = *(int*) (serializer + 0x28);
        if (count <= 0 || count > 4096)
        {
            return 0;
        }

        var arr = *(nint*) (serializer + 0x30);
        if (!NativeUtil.IsUserPtr(arr))
        {
            return 0;
        }

        for (var i = 0; i < count; i++)
        {
            var rec   = arr + (nint) i * 0x2E;
            var child = *(nint*) (rec + 0x08);
            if (NativeUtil.IsUserPtr(child))
            {
                var found = WalkToLeafRec(child, ref current, target, depth + 1);
                if (found != 0)
                {
                    return found;
                }

                continue;
            }

            if (current == target)
            {
                return rec;
            }

            current++;
        }

        return 0;
    }

    // Largest plausible field-array index — backstop when the array length reads implausibly. A serializer
    // never has anywhere near this many fields, and arr + 4096*0x2E (~0x2C000) stays within the array's
    // mapped region, so a bounded-but-wrong index reads garbage (→ no match → passthrough) instead of
    // faulting on an unmapped page.
    private const int MaxFieldArrayIndex = 4096;

    // True if idx is a valid index into serializer's field array. The CUtlVector element count sits just
    // before the data pointer (count @ serializer+0x28, data @ +0x30); use it when sane, else the cap.
    private static bool IndexInBounds(nint serializer, short idx)
    {
        if (idx < 0)
        {
            return false;
        }

        var count = *(int*) (serializer + 0x28);
        var limit = (count > 0 && count <= MaxFieldArrayIndex) ? count : MaxFieldArrayIndex;

        return idx < limit;
    }

    // Classify the field's encoder family by its live dispatch fn (slot 0 of the dispatch object at
    // fieldInfo+0x38) against the gamedata-resolved encoder map. An unknown fn yields Unsupported, so the
    // caller passes through and never corrupts bits. IsUserPtr-guarded, no try/catch.
    private static FieldType Classify(nint leafRec)
    {
        var map = _encoderTypes;
        if (map is null || !NativeUtil.IsUserPtr(leafRec))
        {
            return FieldType.Unsupported;
        }

        var fieldInfo = *(nint*) (leafRec + 0x00);
        if (!NativeUtil.IsUserPtr(fieldInfo))
        {
            return FieldType.Unsupported;
        }

        // The encode fn is vtable SLOT 0 of the dispatch object at fieldInfo+0x38 (= *(*(fieldInfo+0x38))),
        // the same address UniformEncoderHook keys on and the map is built from. (Reading +0x30 here — the
        // registry-entry fn offset — was the bug that made every per-client field classify Unsupported.)
        var dispatch = *(nint*) (fieldInfo + 0x38);
        if (!NativeUtil.IsUserPtr(dispatch))
        {
            return FieldType.Unsupported;
        }

        var encoderFn = *(nint*) dispatch;
        if (!NativeUtil.IsUserPtr(encoderFn))
        {
            return FieldType.Unsupported;
        }

        return map.TryGetValue(encoderFn, out var t) ? t : FieldType.Unsupported;
    }

    // Largest value struct layout the substitute path builds (quant struct ~0x30; byte-array struct needs
    // the count slot at +0x28) fits in 0x30 bytes.
    private const int LiveScratchSize = 0x30;

    // Scratch bf_write header size. Real bf_write uses fields through +0x22 (flag); 0x40 gives slack.
    private const int ScratchBfSize = 0x40;

    // Upper bound (bytes) for a string / byte-array substitute. The substitute buffer is stackalloc'd on
    // the engine send worker thread, so a caller-controlled size must be bounded; oversized values pass
    // the real value through. 4 KiB comfortably covers names and typical small arrays.
    private const int MaxSubstituteBytes = 4096;

    // Data-region array at snapshotPtr+0x30 holds at most 15 entries (EncodeField stack buffer width).
    private const int MaxRegionId = 14;

    // Reconstruct the live entity valuePtr for a quantized-float field from the captured snapshot:
    //   valuePtr = *(nint*)(snapshotPtr + 0x30 + regionId*8) + *(ushort*)(fieldInfo + 0x20)
    //   regionId = *(byte*)(leafRec + 0x2C)
    // IsUserPtr-guarded; returns false on any guard failure.
    private static bool TryGetLiveValuePtr(nint leafRec, out nint valuePtr)
    {
        valuePtr = 0;

        var snapshotPtr = _currentSnapshotPtr;
        if (!NativeUtil.IsUserPtr(snapshotPtr))
        {
            return false;
        }

        var fieldInfo = *(nint*) (leafRec + 0x00);
        if (!NativeUtil.IsUserPtr(fieldInfo))
        {
            return false;
        }

        var regionId = (uint) *(byte*) (leafRec + 0x2C);
        if (regionId > MaxRegionId)
        {
            return false;
        }

        var fieldOffset    = (uint) *(ushort*) (fieldInfo + 0x20);
        var regionArrayPtr = snapshotPtr + 0x30 + (nint) (regionId * 8);
        var dataRegionBase = *(nint*) regionArrayPtr;
        if (!NativeUtil.IsUserPtr(dataRegionBase))
        {
            return false;
        }

        var candidate = dataRegionBase + (nint) fieldOffset;
        if (!NativeUtil.IsUserPtr(candidate))
        {
            return false;
        }

        valuePtr = candidate;

        return true;
    }

    // Build the fn-pointer -> FieldType map once at Install from the gamedata-resolved bucket handler
    // bases. Each bucket's encoder entries (stride 0x80) carry the encoder-name at +0x00 and the fn at
    // +0x30; the (bucket, name) pair determines the FieldType, exactly as the engine's wiring does. These
    // reads happen once here, off the hot path (Classify is then a dictionary lookup). The single
    // install-time try guards the whole enumeration; a fault yields a partial/empty map (safe passthrough).
    private static Dictionary<nint, FieldType> BuildEncoderTypeMap(ILogger logger)
    {
        var map = new Dictionary<nint, FieldType>();

        if (_bucketAddrs.Length == 0)
        {
            logger.LogWarning("FieldSubstitution: no encoder bucket bases resolved — encoder map empty (all fields Unsupported)");

            return map;
        }

        try
        {
            for (var i = 0; i < _bucketAddrs.Length && i < BucketIndices.Length; i++)
            {
                var bucket  = BucketIndices[i];
                var handler = _bucketAddrs[i];

                if (!NativeUtil.IsUserPtr(handler))
                {
                    continue;
                }

                var count = ReadBucketCount(bucket);
                if (count <= 0 || count > 32)
                {
                    continue;
                }

                for (var e = 0; e < count; e++)
                {
                    var entry = handler + e * 0x80;
                    var fn    = *(nint*) (entry + 0x30);
                    if (!NativeUtil.IsUserPtr(fn))
                    {
                        continue;
                    }

                    var type = ClassifyEntry(bucket, *(nint*) (entry + 0x00));
                    if (type == FieldType.Unsupported)
                    {
                        continue;
                    }

                    if (type == FieldType.Int32 && NativeUtil.IsUserPtr(_encodeInt32Addr) && fn != _encodeInt32Addr)
                    {
                        logger.LogWarning(
                            "FieldSubstitution: bucket-1 default fn=0x{Fn:X} != gamedata EncodeInt32 (0x{Sig:X}) — classification may be stale",
                            fn, _encodeInt32Addr);
                    }

                    map[fn] = type;
                }
            }

            logger.LogInformation("FieldSubstitution: encoder type map built — {Count} entries", map.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "FieldSubstitution: BuildEncoderTypeMap faulted — partial or empty map");
        }

        return map;
    }

    private static int ReadBucketCount(int bucket)
        => NativeUtil.IsUserPtr(_registryAddr) ? *(int*) (_registryAddr + bucket * 16 + 0x08) : 0;

    // Map (bucket, encoder-name) -> FieldType, matching the engine's encoder-registry semantics.
    // Known encoder names as UTF8 bytes — classification byte-compares the registry entry's name char*
    // against these (no managed string built at load).
    private static readonly byte[] _enDefault       = "default"u8.ToArray();
    private static readonly byte[] _enFixed32        = "fixed32"u8.ToArray();
    private static readonly byte[] _enFixed64        = "fixed64"u8.ToArray();
    private static readonly byte[] _enQangle         = "qangle"u8.ToArray();
    private static readonly byte[] _enQanglePitchYaw = "qangle_pitch_yaw"u8.ToArray();
    private static readonly byte[] _enQanglePrecise  = "qangle_precise"u8.ToArray();
    private static readonly byte[] _enNormal         = "normal"u8.ToArray();
    private static readonly byte[] _enCoord          = "coord"u8.ToArray();
    private static readonly byte[] _enCoordIntegral  = "coord_integral"u8.ToArray();

    // Map an encoder registry entry's name char* (by bucket) to a FieldType via byte-compare. Bucket-3 names
    // are exactly: default, qangle, normal, coord, coord_integral, qangle_pitch_yaw, qangle_precise. The
    // "default" entry IS the quantized-float encoder (single-component quantized floats like m_flViewmodelFOV);
    // the two extra qangle variants read the same float lanes as qangle.
    private static FieldType ClassifyEntry(int bucket, nint name)
    {
        var def = NativeUtil.NameEquals(name, _enDefault);

        return bucket switch
        {
            1 => def ? FieldType.Int32
                : NativeUtil.NameEquals(name, _enFixed32) ? FieldType.Fixed32
                : NativeUtil.NameEquals(name, _enFixed64) ? FieldType.Fixed64
                : FieldType.Unsupported,
            2 => def ? FieldType.UInt32
                : NativeUtil.NameEquals(name, _enFixed32) ? FieldType.Fixed32
                : NativeUtil.NameEquals(name, _enFixed64) ? FieldType.Fixed64
                : FieldType.Unsupported,
            3 => def ? FieldType.QuantizedFloat
                : NativeUtil.NameEquals(name, _enQangle)
                  || NativeUtil.NameEquals(name, _enQanglePitchYaw)
                  || NativeUtil.NameEquals(name, _enQanglePrecise) ? FieldType.QAngle3
                : NativeUtil.NameEquals(name, _enNormal) ? FieldType.Normal3
                : NativeUtil.NameEquals(name, _enCoord) ? FieldType.Coord3
                : NativeUtil.NameEquals(name, _enCoordIntegral) ? FieldType.CoordIntegral3
                : FieldType.Unsupported,
            4 => def ? FieldType.Float32 : FieldType.Unsupported,
            5 => def ? FieldType.String : FieldType.Unsupported,
            6 => def ? FieldType.ByteArray : FieldType.Unsupported,
            7 => def ? FieldType.Bool : FieldType.Unsupported,
            _ => FieldType.Unsupported,
        };
    }

    private static bool ValidateAddresses(ILogger logger)
    {
        var ok = true;

        // On Linux the field-path is captured via GetBitRange; on Windows GetBitRange is inlined and
        // the per-field site address is used instead. Require the correct one for the current platform.
        if (OperatingSystem.IsWindows())
        {
            if (WindowsFieldPathSiteAddr == 0)
            {
                logger.LogWarning("FieldSubstitution: WriteFieldList_FieldPathSite address not resolved (Windows)");
                ok = false;
            }
        }
        else
        {
            if (GetBitRangeAddr == 0)
            {
                logger.LogWarning("FieldSubstitution: GetBitRange address not resolved");
                ok = false;
            }
        }

        if (ValueCopyAddr == 0)
        {
            logger.LogWarning("FieldSubstitution: ValueCopy address not resolved");
            ok = false;
        }

        if (WriteFieldListAddr == 0)
        {
            logger.LogWarning("FieldSubstitution: WriteFieldList address not resolved");
            ok = false;
        }

        return ok;
    }
}
