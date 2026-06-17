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
using System.Threading;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Hooks;
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

internal enum SubstitutionMode
{
    Off,
    Fake,
}

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

// CallbackKind maps a SpoofValue.Kind (or uniform-value Kind) to the set of FieldTypes it can drive.
// Kept internal so CallbackMatches can gate the dispatch without exposing it to consumers.
internal enum CallbackKind
{
    None = 0,
    Int,
    Float,
    Bool,
    Vector,
    String,
    Bytes,
}

internal static unsafe class FieldSubstitution
{
    private static volatile int _mode = (int) SubstitutionMode.Off;

    public static SubstitutionMode Mode
    {
        get => (SubstitutionMode) _mode;
        set => Interlocked.Exchange(ref _mode, (int) value);
    }

    // A registration holds either a uniform SpoofValue (no Callback) or a per-client SendProxyCallback
    // (with Value seeded as the initial value passed to the callback). Key entityIndex: -1 = all entities
    // (global), >= 0 = a specific entity. ValueCopyHook probes the entity-specific entry first, then the
    // global fallback — both lock-free dictionary reads.
    private readonly struct SpoofEntry
    {
        public readonly SpoofValue         Value;
        public readonly SendProxyCallback? Callback;

        // Assembly name of the module that owns this callback (null for uniform spoofs, which hold no
        // delegate). Used to purge a consumer's callbacks when its module unloads — invoking a delegate
        // into an unloaded AssemblyLoadContext would crash the server.
        public readonly string? Owner;

        // One-shot (SendFake) support. When OneShot is set the registration fires for exactly the
        // TargetClient on its next transmit and is then removed; sends to other clients pass through
        // untouched. TargetClient is the CServerSideClient* (matched against RecipientCapture.CurrentClient).
        public readonly bool OneShot;
        public readonly nint TargetClient;

        // Uniform spoof: store value directly, no callback.
        public SpoofEntry(in SpoofValue value) : this()
            => Value = value;

        // One-shot (SendFake): a uniform-style value bound to a single target client.
        public SpoofEntry(in SpoofValue value, nint targetClient) : this()
        {
            Value        = value;
            OneShot      = true;
            TargetClient = targetClient;
        }

        // Per-client callback; Value carries the optional seed (default zero for that kind).
        public SpoofEntry(SendProxyCallback callback, in SpoofValue seed) : this()
        {
            Value    = seed;
            Callback = callback;
            Owner    = OwnerOf(callback);
        }

        // The defining assembly of the callback target = the consumer module that registered it.
        private static string? OwnerOf(Delegate callback)
            => callback.Method.Module.Assembly.GetName().Name;

        public bool HasCallback => Callback is not null;

        // Derive a CallbackKind from SpoofValue.Kind so CallbackMatches can gate by field family.
        // A SpoofKind.Int seed is compatible with the full integer family (int/uint/bool/fixed).
        private static CallbackKind KindOf(SpoofKind k)
            => k switch
            {
                SpoofKind.Int    => CallbackKind.Int,
                SpoofKind.Float  => CallbackKind.Float,
                SpoofKind.Bool   => CallbackKind.Bool,
                SpoofKind.Vector => CallbackKind.Vector,
                SpoofKind.String => CallbackKind.String,
                SpoofKind.Bytes  => CallbackKind.Bytes,
                _                => CallbackKind.None,
            };

        // True when this registration's value kind can drive the given field family. A mismatch (e.g.
        // a string value on an int field) would write wrong-type bits, so the caller passes through.
        public bool CallbackMatches(FieldType type)
            => KindOf(Value.Kind) switch
            {
                CallbackKind.Int    => type is FieldType.Int32 or FieldType.UInt32 or FieldType.Int64 or FieldType.Bool or FieldType.Fixed32 or FieldType.Fixed64,
                CallbackKind.Float  => type is FieldType.Float32 or FieldType.Coord3 or FieldType.Normal3 or FieldType.CoordIntegral3 or FieldType.QuantizedFloat,
                CallbackKind.Bool   => type is FieldType.Bool,
                CallbackKind.Vector => type is FieldType.QAngle3 or FieldType.Vector3 or FieldType.Coord3 or FieldType.Normal3 or FieldType.CoordIntegral3 or FieldType.QuantizedFloat,
                CallbackKind.String => type is FieldType.String,
                CallbackKind.Bytes  => type is FieldType.ByteArray,
                _                   => false,
            };
    }

    private static readonly ConcurrentDictionary<(string ser, string field, int entityIndex), SpoofEntry> _registry = new();

    // -- Registration (called from SendProxyManager on the main thread) ----------------------------

    public static void SetSpoof(string ser, string field, in SpoofValue value)
        => _registry[(ser, field, -1)] = new SpoofEntry(value);

    public static void SetCallback(string ser, string field, SendProxyCallback cb, in SpoofValue seed = default)
        => _registry[(ser, field, -1)] = new SpoofEntry(cb, seed);

    public static void SetEntitySpoof(int ent, string ser, string field, in SpoofValue value)
        => _registry[(ser, field, ent)] = new SpoofEntry(value);

    public static void SetEntityCallback(int ent, string ser, string field, SendProxyCallback cb, in SpoofValue seed = default)
        => _registry[(ser, field, ent)] = new SpoofEntry(cb, seed);

    // One-shot (SendFake): fire once for targetClient on the next transmit, then auto-remove. Entity-scoped
    // (ent >= 0) so it never lingers across entity-index reuse; the force-dirty is issued by the caller.
    public static void SetOneShot(int ent, string ser, string field, nint client, in SpoofValue value)
        => _registry[(ser, field, ent)] = new SpoofEntry(value, client);

    public static void ClearGlobal(string ser, string field)            => _registry.TryRemove((ser, field, -1), out _);
    public static void ClearEntity(int ent, string ser, string field)   => _registry.TryRemove((ser, field, ent), out _);
    public static void ClearAll()                                       => _registry.Clear();

    // Remove every callback registration owned by an unloading module. A delegate into an unloaded
    // AssemblyLoadContext would crash the server when the send path invokes it, so this is called from
    // OnLibraryDisconnect. Uniform spoofs (Owner == null) hold no delegate and are left untouched.
    public static int PurgeOwner(string moduleName)
    {
        var removed = 0;
        foreach (var kv in _registry)
        {
            if (kv.Value.Owner == moduleName && _registry.TryRemove(kv.Key, out _))
            {
                removed++;
            }
        }

        return removed;
    }

    // Drop every registration scoped to a specific entity index (called from OnEntityDeleted — indices
    // are reused after disconnect/round restart, so stale entity-scoped registrations must not persist).
    public static void ClearEntityIndex(int entityIndex)
    {
        if (entityIndex < 0)
        {
            return;
        }

        foreach (var key in _registry.Keys)
        {
            if (key.entityIndex == entityIndex)
            {
                _registry.TryRemove(key, out _);
            }
        }
    }

    public static bool IsHooked(string ser, string field)
    {
        foreach (var key in _registry.Keys)
        {
            if (key.ser == ser && key.field == field)
            {
                return true;
            }
        }

        return false;
    }

    // CFieldPath* filled by GetBitRange; valid for the BitCopyPrimitive call that follows on this thread.
    [ThreadStatic]
    private static nint _currentFieldPath;

    // CFlattenedSerializer* captured by the WriteFieldList shim (rdi).
    [ThreadStatic]
    private static nint _currentSerializer;

    // Entity index from WriteDeltaEntity ctx+0x34; -1 if not captured.
    [ThreadStatic]
    private static int _currentEntityIndex;

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

    public static nint GetBitRangeAddr;
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
        _logger = logger;

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
            var gbrHook   = bridge.HookManager.CreateMidFuncHook();
            var gbrHookFn = (nint) (delegate* unmanaged[Cdecl]<MidHookContext*, void>) &GetBitRangePathHook;
            gbrHook.Prepare(GetBitRangeAddr, gbrHookFn);

            if (!gbrHook.Install())
            {
                logger.LogWarning("FieldSubstitution: GetBitRange mid-hook Install() failed");
                DisposeHook(ref _wflShimHook, ref _wflShimTrampoline);

                return false;
            }

            _getBitRangeMidHook = gbrHook;
            logger.LogInformation("FieldSubstitution: GetBitRange mid-hook installed @ 0x{Addr:X}", GetBitRangeAddr);
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
        Mode = SubstitutionMode.Off;

        DisposeHook(ref _valueCopyHook, ref _valueCopyTrampoline);
        DisposeMidHook(ref _getBitRangeMidHook);
        DisposeHook(ref _wflShimHook, ref _wflShimTrampoline);
        DisposeHook(ref _wdeEntityCaptureHook, ref _wdeEntityCaptureTrampoline);
        ForceResend.Uninstall();

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

    /// <summary>
    ///     (serializer, field) pairs registered for an entity (its own index or the global -1 entries) —
    ///     used by <see cref="ForceResend"/> to know which fields to force into that entity's delta.
    /// </summary>
    public static IEnumerable<(string ser, string field)> RegistryFieldsForEntity(int entityIndex)
    {
        foreach (var key in _registry.Keys)
        {
            if (key.entityIndex == entityIndex || key.entityIndex == -1)
            {
                yield return (key.ser, key.field);
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static nint WflShim(
        nint a,
        nint b, nint c, nint d, nint e,
        uint p6,
        uint p7, nint p8, uint p9)
    {
        // No per-client registrations → nothing to substitute below; skip the serializer capture entirely.
        if (_registry.IsEmpty)
        {
            return ((delegate* unmanaged[Cdecl]<
                nint, nint, nint, nint, nint, uint,
                uint, nint, uint,
                nint>) _wflShimTrampoline)(a, b, c, d, e, p6, p7, p8, p9);
        }

        _currentSerializer = a;

        // Force-resend (off by default): cache this serializer's flattened field-index map the first time
        // it's seen, so the WriteFields hook can resolve a hooked field name -> index. No-op when disabled.
        if (ForceResend.Enabled)
        {
            ForceResend.NoteSerializer(NativeUtil.ReadShortAscii(*(nint*) (a + 0x00), 48), a);
        }

        var result = ((delegate* unmanaged[Cdecl]<
            nint, nint, nint, nint, nint, uint,
            uint, nint, uint,
            nint>) _wflShimTrampoline)(a, b, c, d, e, p6, p7, p8, p9);
        _currentSerializer = 0;

        return result;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static nint WdeEntityCaptureHook(nint a, nint b, nint c, nint d, nint e, nint f)
    {
        // No per-client registrations → skip the entity-index/snapshot capture.
        if (_registry.IsEmpty)
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
        if (_registry.IsEmpty)
        {
            return;
        }

        var pathOut = ReadFieldPathRegister(ctx);
        _currentFieldPath = NativeUtil.IsUserPtr(pathOut) ? pathOut : 0;
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
        if (_registry.IsEmpty)
        {
            return CallOriginal(dst, src, bitcount);
        }

        var mode = (SubstitutionMode) _mode;
        if (mode == SubstitutionMode.Off || !NativeUtil.IsUserPtr(_currentSerializer))
        {
            return CallOriginal(dst, src, bitcount);
        }

        var pathPtr = _currentFieldPath;
        if (!NativeUtil.IsUserPtr(pathPtr))
        {
            return CallOriginal(dst, src, bitcount);
        }

        try
        {
            var serPtr    = _currentSerializer;
            var serName   = NativeUtil.ReadShortAscii(*(nint*) (serPtr + 0x00), 48);
            var fieldName = ResolveFieldName(serPtr, pathPtr, out var leafRec);
            if (fieldName.Length == 0)
            {
                return CallOriginal(dst, src, bitcount);
            }

            var client      = RecipientCapture.CurrentClient;
            var entityIndex = _currentEntityIndex;

            var hasReg = TryGetRegistration(serName, fieldName, entityIndex, out var reg, out var matchedEnt);

            // One-shot (SendFake): only the target client gets the fake; everyone else passes through and
            // the registration survives until its target is served. Matched on CServerSideClient*.
            if (hasReg && reg.OneShot && client != reg.TargetClient)
            {
                return CallOriginal(dst, src, bitcount);
            }

            if (!hasReg)
            {
                return CallOriginal(dst, src, bitcount);
            }

            var fieldType = Classify(leafRec);
            if (fieldType == FieldType.Unsupported)
            {
                return CallOriginal(dst, src, bitcount);
            }

            // Seed a SpoofValue from the registration's stored value (uniform) or the zero default, then
            // optionally invoke the per-client callback to let it mutate the value. The per-client callback
            // is the one genuine managed throw-site on this path; a throw is logged and passed through.

            // Build the initial SpoofValue seeded from the registration's stored value.
            var spoofVal = reg.Value;

            // For the uniform case (no callback), we apply directly. For the callback case, we first
            // check kind compatibility (same rule as before), then invoke once.
            bool substitute;

            if (!reg.HasCallback)
            {
                substitute = true;
            }
            else if (!reg.CallbackMatches(fieldType))
            {
                // A callback with a kind incompatible with this field family would write garbage — pass through.
                return CallOriginal(dst, src, bitcount);
            }
            else
            {
                try
                {
                    substitute = reg.Callback!(client, entityIndex, ref spoofVal);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "SendProxy: per-client callback threw for \"{Ser}::{Field}\" — passing through", serName, fieldName);

                    return CallOriginal(dst, src, bitcount);
                }
            }

            if (!substitute)
            {
                return CallOriginal(dst, src, bitcount);
            }

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

            var copyOk = CallOriginal(dst, (nint) bw, (uint) encodedBits);

            // One-shot fired for its target client — remove it so it doesn't recur. Other clients on this
            // snapshot already passed through (client-match gate above), so they never see the fake.
            if (reg.OneShot)
            {
                _registry.TryRemove((serName, fieldName, matchedEnt), out _);
            }

            return copyOk;
        }
        catch
        {
            // Unmanaged-callback boundary: never let a managed exception escape into engine code. Reached
            // only for failures BEFORE the native emit sequence above, so a plain passthrough is correct.
            return CallOriginal(dst, src, bitcount);
        }
    }

    private static bool TryGetRegistration(string serName, string fieldName, int entityIndex, out SpoofEntry reg, out int matchedEnt)
    {
        if (entityIndex >= 0 && _registry.TryGetValue((serName, fieldName, entityIndex), out reg))
        {
            matchedEnt = entityIndex;

            return true;
        }

        matchedEnt = -1;

        return _registry.TryGetValue((serName, fieldName, -1), out reg);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte CallOriginal(nint dst, nint src, uint bitcount)
        => ((delegate* unmanaged[Cdecl]<nint, nint, uint, byte>) _valueCopyTrampoline)(dst, src, bitcount);

    // Walk the CFieldPath (filled by GetBitRange) through the flattened-serializer field array to the leaf
    // record + its field name. Every dereference is IsUserPtr-guarded; a guard failure returns empty (the
    // caller passes through). RE layout: docs/REVERSE_ENGINEERING.md §9b.
    private static string ResolveFieldName(nint serializer, nint hdr, out nint leafRec)
    {
        leafRec = 0;
        if (!NativeUtil.IsUserPtr(serializer) || !NativeUtil.IsUserPtr(hdr))
        {
            return string.Empty;
        }

        // Path levels. CFieldPath is at most 3 deep (§9a); a larger/garbage count means a stale or bogus
        // path — bail rather than walk it.
        var count = *(short*) (hdr + 0x18);
        if (count < 1 || count > 3)
        {
            return string.Empty;
        }

        nint idxArr;
        if (*(byte*) (hdr + 0x1A) != 0)
        {
            idxArr = *(nint*) hdr;
            if (!NativeUtil.IsUserPtr(idxArr))
            {
                return string.Empty;
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
                    return string.Empty;
                }

                break;
            }

            // For levels > 0, descend into the child serializer of the current record first.
            if (k > 0)
            {
                var child = *(nint*) (rec + 0x08);
                if (!NativeUtil.IsUserPtr(child))
                {
                    return string.Empty;
                }

                serArr = child;
            }

            // Bound the index against the serializer's field-array length so a stale/garbage index can
            // never deref off the array (the fatal-AV path). The length is the CUtlVector size that
            // precedes the data pointer at +0x30; fall back to a hard cap if it reads implausibly.
            if (!IndexInBounds(serArr, idxK))
            {
                return string.Empty;
            }

            var arr = *(nint*) (serArr + 0x30);
            if (!NativeUtil.IsUserPtr(arr))
            {
                return string.Empty;
            }

            rec = arr + idxK * 0x2E;
            if (!NativeUtil.IsUserPtr(rec))
            {
                return string.Empty;
            }
        }

        var pInfo = *(nint*) (rec + 0x00);
        if (!NativeUtil.IsUserPtr(pInfo))
        {
            return string.Empty;
        }

        leafRec = rec;

        return NativeUtil.ReadShortAscii(*(nint*) (pInfo + 0x08), 48);
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

                    var name = NativeUtil.ReadShortAscii(*(nint*) (entry + 0x00), 32);
                    var type = ClassifyEntry(bucket, name);
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
    private static FieldType ClassifyEntry(int bucket, string name)
        => bucket switch
        {
            1 => name switch { "default" => FieldType.Int32, "fixed32" => FieldType.Fixed32, "fixed64" => FieldType.Fixed64, _ => FieldType.Unsupported },
            2 => name switch { "default" => FieldType.UInt32, "fixed32" => FieldType.Fixed32, "fixed64" => FieldType.Fixed64, _ => FieldType.Unsupported },
            // Bucket-3 entry names are exactly (verified from the registry dump): default, qangle, normal,
            // coord, coord_integral, qangle_pitch_yaw, qangle_precise. The "default" entry IS the quantized
            // float encoder (single-component quantized floats like m_flViewmodelFOV); the two extra qangle
            // variants read the same 2–3 float lanes as qangle. (Earlier "vector3"/"quantized" names were
            // wrong — they don't exist in the registry — which left default/pitch_yaw/precise unhooked.)
            3 => name switch
            {
                "default"          => FieldType.QuantizedFloat,
                "qangle"           => FieldType.QAngle3,
                "qangle_pitch_yaw" => FieldType.QAngle3,
                "qangle_precise"   => FieldType.QAngle3,
                "normal"           => FieldType.Normal3,
                "coord"            => FieldType.Coord3,
                "coord_integral"   => FieldType.CoordIntegral3,
                _                  => FieldType.Unsupported,
            },
            4 => name == "default" ? FieldType.Float32 : FieldType.Unsupported,
            5 => name == "default" ? FieldType.String : FieldType.Unsupported,
            6 => name == "default" ? FieldType.ByteArray : FieldType.Unsupported,
            7 => name == "default" ? FieldType.Bool : FieldType.Unsupported,
            _ => FieldType.Unsupported,
        };

    private static bool ValidateAddresses(ILogger logger)
    {
        var ok = true;
        if (GetBitRangeAddr == 0)
        {
            logger.LogWarning("FieldSubstitution: GetBitRange address not resolved");
            ok = false;
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
