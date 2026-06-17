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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Hooks;

namespace YappersHQ.SendProxy.Native;

/// <summary>
///     Force-resend (live application of a per-client spoof). RE: the CS2 entity delta is value-compared,
///     so a freshly-registered spoof on an unchanged field never enters the delta until a full update.
///     This hooks the serializer singleton's encode-fields virtual (vtable slot 14 / +0x70) — the consumer
///     that BOTH delta-build paths feed their field-index list to — and, for a hooked entity, sorted-inserts
///     the hooked field's index into that list so it gets encoded (and the value-substitution hook then
///     fakes it for the issuer). See docs/FORCE_RESEND.md for the full RE + the cross-platform confirmation
///     (Linux global 0xac4ae0 / Windows 0x1806858a0, identical slot 8 = CalcDelta + slot 14 = WriteFields).
///
///     OFF by default and the vtable hook is NOT installed until <see cref="Enabled"/> is turned on — so
///     the validated per-client substitution path is untouched at runtime unless explicitly enabled.
///
///     Encode-fields signature (RE'd, 6 args, returns int):
///       int WriteFields(self, fromData, toFieldCount, fieldList /*arg3, CUtlVector&lt;int&gt;*/, entIdx /*arg4*/, flag)
/// </summary>
internal static unsafe class ForceResend
{
    /// <summary>Master switch. While false the hook is never installed; flipping it true installs it.</summary>
    public static bool Enabled { get; private set; }

    // The encode-fields fn is virtual slot 14 (+0x70 / 8) of the serializer singleton; slot 8 (+0x40) is CalcDelta.
    private const int WriteFieldsVTableIndex = 14;

    private static IVirtualHook?    _hook;
    private static nint             _trampoline;
    private static nint             _singletonGlobal; // gamedata-resolved address of the global SLOT (holds the object ptr)
    private static InterfaceBridge? _bridge;
    private static ILogger?         _logger;

    // serializerName -> (fieldName -> flattened-leaf index). Built lazily from the serializer record walk
    // (FieldSubstitution.WflShim calls NoteSerializer while enabled). The leaf-index numbering is the one
    // value derived by construction (DFS over the serializer's records, descending embedded child
    // serializers) rather than read from the engine — verify it on first enable (see docs/FORCE_RESEND.md).
    private static readonly ConcurrentDictionary<string, Dictionary<string, int>> _leafIndex = new();

    private static long _injected; // diag-free counter of successful injections (telemetry only)
    private static long _skippedFull; // list-at-capacity skips (no grow performed)

    /// <summary>Provide the bridge/logger + the gamedata-resolved serializer-singleton global slot address.
    ///     Does NOT install the hook (that happens lazily on first <see cref="SetEnabled"/>(true)).</summary>
    public static void Configure(InterfaceBridge bridge, ILogger logger, nint globalSlotAddr)
    {
        _bridge          = bridge;
        _logger          = logger;
        _singletonGlobal = globalSlotAddr;
    }

    /// <summary>Cache the flattened-leaf field-index map for a serializer the first time it is seen.</summary>
    public static void NoteSerializer(string serName, nint serializer)
    {
        if (!Enabled || serName.Length == 0 || _leafIndex.ContainsKey(serName) || !NativeUtil.IsUserPtr(serializer))
        {
            return;
        }

        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        var next = 0;
        try
        {
            WalkLeaves(serializer, map, ref next, 0);
        }
        catch
        {
            return; // never let a bad walk poison the cache or escape
        }

        _leafIndex.TryAdd(serName, map);
    }

    // DFS over the serializer field records (base @serializer+0x30, stride 0x2E; record: leaf fieldInfo @+0x00,
    // child serializer @+0x08; name = *(fieldInfo+0x08)). Embedded fields descend the child serializer so their
    // leaves get sequential indices — matching the flattened index space the delta list uses.
    private static void WalkLeaves(nint serializer, Dictionary<string, int> map, ref int next, int depth)
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
                WalkLeaves(child, map, ref next, depth + 1);
                continue;
            }

            var fieldInfo = *(nint*) (rec + 0x00);
            if (!NativeUtil.IsUserPtr(fieldInfo))
            {
                next++;
                continue;
            }

            var name = NativeUtil.ReadShortAscii(*(nint*) (fieldInfo + 0x08), 48);
            if (name.Length != 0)
            {
                map[name] = next;
            }

            next++;
        }
    }

    public static bool Install(InterfaceBridge bridge, ILogger logger)
    {
        _bridge = bridge;
        _logger = logger;

        if (_hook is not null)
        {
            return true;
        }

        if (!NativeUtil.IsUserPtr(_singletonGlobal))
        {
            logger.LogWarning("ForceResend: serializer-singleton global not resolved — cannot install");

            return false;
        }

        var obj = *(nint*) _singletonGlobal;
        if (!NativeUtil.IsUserPtr(obj))
        {
            logger.LogWarning("ForceResend: serializer singleton is null");

            return false;
        }

        var vtable = *(nint*) obj;
        if (!NativeUtil.IsUserPtr(vtable))
        {
            logger.LogWarning("ForceResend: serializer vtable invalid");

            return false;
        }

        var hook   = bridge.HookManager.CreateVirtualHook();
        var hookFn = (nint) (delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, int>) &WriteFieldsHook;
        hook.Prepare(vtable, WriteFieldsVTableIndex, hookFn);

        if (!hook.Install())
        {
            logger.LogWarning("ForceResend: IVirtualHook.Install() failed");

            return false;
        }

        _hook       = hook;
        _trampoline = hook.Trampoline;
        logger.LogInformation("ForceResend: WriteFields hook installed (vtable=0x{V:X} slot {S})", vtable, WriteFieldsVTableIndex);

        return true;
    }

    public static void Uninstall()
    {
        if (_injected != 0 || _skippedFull != 0)
        {
            _logger?.LogInformation("ForceResend: injected={Injected} skippedFull={Skipped}", _injected, _skippedFull);
        }

        _hook?.Uninstall();
        _hook = null;
        _trampoline = 0;
        _leafIndex.Clear();
    }

    /// <summary>Enable/disable. Installs the vtable hook on first enable; leaves it installed but inert when off.</summary>
    public static bool SetEnabled(bool on)
    {
        if (on && _hook is null && _bridge is { } b && _logger is { } l && !Install(b, l))
        {
            return false;
        }

        Enabled = on;

        return true;
    }

    // vtable slot 14 — encode-fields. For a hooked entity, sorted-insert the hooked field indices into the
    // field-index list (arg3) before the original encodes from it. ABI-portable positional args.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int WriteFieldsHook(nint self, nint fromData, nint toFieldCount, nint fieldList, nint entIdx, nint flag)
    {
        if (Enabled && NativeUtil.IsUserPtr(fieldList))
        {
            try
            {
                InjectHookedFields((int) entIdx, fieldList);
            }
            catch
            {
                // never let the force-path escape into the engine call
            }
        }

        return ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, int>) _trampoline)
            (self, fromData, toFieldCount, fieldList, entIdx, flag);
    }

    private static void InjectHookedFields(int entIdx, nint listPtr)
    {
        foreach (var (serName, fieldName) in FieldSubstitution.RegistryFieldsForEntity(entIdx))
        {
            if (_leafIndex.TryGetValue(serName, out var map) && map.TryGetValue(fieldName, out var idx))
            {
                CUtlVectorSortedInsert(listPtr, idx);
            }
        }
    }

    // CUtlVector<int> at listPtr: { int size @+0x00, int allocAndFlag @+0x04 (&0x7fffffff = capacity),
    //   data @+0x08 = inline int[4] when capacity<=4 else *(int**)(listPtr+8) }. Sorted (ascending) insert,
    // skipping if already present. Does NOT grow (engine-allocator surgery) — if the list is at capacity it
    // is left untouched and counted (the field then rides a later tick that has room, or a real change).
    private static void CUtlVectorSortedInsert(nint listPtr, int value)
    {
        var size = *(int*) listPtr;
        var cap  = *(int*) (listPtr + 0x04) & 0x7FFFFFFF;
        if (size < 0 || size > 4096 || cap < size)
        {
            return; // garbage / unexpected shape — leave it alone
        }

        var data = cap > 4 ? *(nint*) (listPtr + 0x08) : (listPtr + 0x08);
        if (!NativeUtil.IsUserPtr(data))
        {
            return;
        }

        var arr = (int*) data;

        // already present? (ascending — stop once we pass it)
        var pos = 0;
        while (pos < size && arr[pos] < value)
        {
            pos++;
        }

        if (pos < size && arr[pos] == value)
        {
            return; // CalcDelta already included it (real change) — nothing to do
        }

        if (size >= cap)
        {
            System.Threading.Interlocked.Increment(ref _skippedFull);

            return; // no inline/heap room and we don't grow with the engine allocator here
        }

        // shift [pos..size) up by one, insert value at pos
        for (var i = size; i > pos; i--)
        {
            arr[i] = arr[i - 1];
        }

        arr[pos]      = value;
        *(int*) listPtr = size + 1;
        System.Threading.Interlocked.Increment(ref _injected);
    }
}
