using System;
using System.Text;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Units;

namespace YappersHQ.SendProxy.Native;

/// <summary>
///     READ-ONLY diagnostic: resolve an entity's serializer the game-function way
///     (vtable slot 0 = <c>GetNetworkSerializerInfo()</c>) and dump the
///     <c>CNetworkSerializerClassInfo</c> head so the runtime offsets (m_pszClassName, m_Fields
///     CUtlVector, field record layout) can be confirmed on the live build before any patch.
///     Touches no engine state — only reads memory.
/// </summary>
internal static class SerializerProbe
{
    /// <summary>Walk entity indices, list the live ones + their class names, then full-Dump the first.</summary>
    public static unsafe void Scan(InterfaceBridge bridge, ILogger logger)
    {
        var found = 0;
        var firstValid = -1;
        for (var i = 0; i < 2048 && found < 24; i++)
        {
            try
            {
                var ent = bridge.EntityManager.FindEntityByIndex((EntityIndex) i);
                if (ent is null) continue;
                var p = ent.GetAbsPtr();
                if (p == 0) continue;
                // ci = vtable[0](entity) = GetNetworkSerializerInfo()
                var ci = ((delegate* unmanaged<nint, nint>) (*(nint*) (*(nint*) p)))(p);
                var cls = ci != 0 ? TryReadAscii(*(nint*) (ci + 0x08)) : "";
                logger.LogInformation("sp_scan idx={Idx} ptr=0x{P:X} class=\"{Cls}\"", i, p, cls);
                if (firstValid < 0) firstValid = i;
                found++;
            }
            catch { /* skip */ }
        }

        logger.LogInformation("sp_scan: {Found} live entities (showing up to 24); dumping first valid idx={First}", found, firstValid);
        if (firstValid >= 0)
            Dump(bridge, logger, firstValid);
    }

    public static unsafe void Dump(InterfaceBridge bridge, ILogger logger, int entityIndex)
    {
        try
        {
            var ent = bridge.EntityManager.FindEntityByIndex((EntityIndex) entityIndex);
            if (ent is null)
            {
                logger.LogWarning("sp_dump: no entity at index {Index}", entityIndex);
                return;
            }

            var entPtr = ent.GetAbsPtr();
            if (entPtr == 0)
            {
                logger.LogWarning("sp_dump: entity {Index} has null native ptr", entityIndex);
                return;
            }

            // vtable slot 0 = CEntityInstance::GetNetworkSerializerInfo() -> CNetworkSerializerClassInfo*.
            var vtbl = *(nint*) entPtr;
            var slot0 = *(nint*) vtbl;
            var getInfo = (delegate* unmanaged<nint, nint>) slot0;
            var classInfo = getInfo(entPtr);

            logger.LogInformation(
                "sp_dump ent={Index}: ptr=0x{Ptr:X} vtbl=0x{Vtbl:X} slot0=0x{Slot:X} classInfo=0x{Class:X}",
                entityIndex, entPtr, vtbl, slot0, classInfo);

            if (classInfo == 0)
                return;

            // CONFIRMED layout: +0x00 m_nHash, +0x08 m_pszClassName, +0x10 field count(int),
            // +0x18 field-array ptr (array of CNetworkSerializerFieldInfo*).
            var className = TryReadAscii(*(nint*) (classInfo + 0x08));
            var fieldCount = *(int*) (classInfo + 0x10);
            var fieldArray = *(nint*) (classInfo + 0x18);
            logger.LogInformation("  class=\"{Cls}\" fieldCount={Cnt} fieldArray=0x{Arr:X}",
                className, fieldCount, fieldArray);

            if (fieldArray == 0 || fieldCount <= 0 || fieldCount > 4096)
                return;

            // Field record layout is already confirmed (m_FieldNameHash +0x00, m_pszFieldName +0x08,
            // m_nFieldSize/m_nFieldOffset +0x38). Only read field[0]'s name via the confirmed offset.
            // NOTE: ANY raw deref of engine memory can hit an unmapped page → AccessViolationException,
            // which is UNCATCHABLE in .NET and aborts the process. So we do the minimum, no speculative
            // qword scanning, no multi-field walk (that's what crashed the server before).
            var rec0 = *(nint*) (fieldArray + 0);
            if (rec0 != 0)
            {
                var nameHash = *(uint*) (rec0 + 0x00);
                var name = TryReadAscii(*(nint*) (rec0 + 0x08));
                logger.LogInformation("  field[0] rec=0x{Rec:X} nameHash=0x{H:X} name=\"{Name}\"", rec0, nameHash, name);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "sp_dump failed for entity {Index}", entityIndex);
        }
    }

    /// <summary>
    ///     READ-ONLY: find the first live entity whose serializer class == <paramref name="classFilter"/>,
    ///     walk its field array for <paramref name="fieldName"/>, and dump the raw qword window of that
    ///     field record (offsets 0x28..0x50) with pointer-likeness tags. This resolves the open question
    ///     of what lives at field+0x38 (encoder-dispatch ptr per the Ghidra decomp, vs m_nFieldSize/Offset
    ///     per an earlier gdb read) BEFORE we swap anything. No writes. Every deref is range-gated.
    /// </summary>
    public static unsafe void DumpField(InterfaceBridge bridge, ILogger logger, string classFilter, string fieldName)
    {
        try
        {
            for (var i = 0; i < 2048; i++)
            {
                var ent = bridge.EntityManager.FindEntityByIndex((EntityIndex) i);
                if (ent is null) continue;
                var p = ent.GetAbsPtr();
                if (p == 0) continue;

                var ci = ((delegate* unmanaged<nint, nint>) (*(nint*) (*(nint*) p)))(p);
                if (ci == 0) continue;
                if (TryReadAscii(*(nint*) (ci + 0x08)) != classFilter) continue;

                var count = *(int*) (ci + 0x10);
                var arr   = *(nint*) (ci + 0x18);
                if (arr == 0 || count <= 0 || count > 4096) continue;

                logger.LogInformation("sp_field: matched class \"{Cls}\" at idx={Idx} (fields={Cnt})",
                    classFilter, i, count);

                for (var f = 0; f < count; f++)
                {
                    var rec = *(nint*) (arr + f * 8);
                    if (!PtrLike(rec)) continue;
                    if (TryReadAscii(*(nint*) (rec + 0x08)) != fieldName) continue;

                    // Found the field record. Dump the qword window + tag pointer-likeness.
                    logger.LogInformation("sp_field: FOUND \"{Cls}::{Field}\" rec=0x{Rec:X}", classFilter, fieldName, rec);
                    for (var off = 0x20; off <= 0x50; off += 0x08)
                    {
                        var q = *(ulong*) (rec + off);
                        var ptr = (nint) q;
                        var tag = PtrLike(ptr) ? "ptr?" : (q < 0x100000 ? "int?" : "----");
                        // If it looks like a pointer, peek one level (vtable) + the would-be slot0.
                        var deref = "";
                        if (PtrLike(ptr))
                        {
                            var v = *(ulong*) ptr;
                            if (PtrLike((nint) v))
                            {
                                var slot0 = *(ulong*) (nint) v;
                                deref = $" -> *=0x{v:X}" + (PtrLike((nint) slot0) ? $" -> **=0x{slot0:X} (code?)" : "");
                            }
                        }
                        logger.LogInformation("  +0x{Off:X2} = 0x{Q:X} [{Tag}]{Deref}", off, q, tag, deref);
                    }
                    return;
                }

                logger.LogWarning("sp_field: class \"{Cls}\" found but no field \"{Field}\"", classFilter, fieldName);
                return;
            }
            logger.LogWarning("sp_field: no live entity with serializer class \"{Cls}\"", classFilter);
        }
        catch (Exception e)
        {
            logger.LogError(e, "sp_field failed");
        }
    }

    // Linux x64 mapped-pointer heuristic: high bytes == 0x00007F.. (heap/.so range). Cheap gate to
    // avoid dereferencing scalar field values (small ints) as pointers and segfaulting.
    private static bool PtrLike(nint p) => p > 0x10000 && ((ulong) p >> 40) == 0x7F;

    // Read up to 31 printable ASCII bytes at p. Gated to Linux x64 heap/rodata range
    // (0x00007Fxx_xxxxxxxx) so we don't dereference scalar field values and segfault.
    private static unsafe string TryReadAscii(nint p)
    {
        if (p <= 0 || ((ulong) p >> 40) != 0x7F)
            return string.Empty;

        try
        {
            var sb = new StringBuilder();
            for (var i = 0; i < 31; i++)
            {
                var b = *(byte*) (p + i);
                if (b == 0)
                    break;
                if (b < 0x20 || b > 0x7E)
                    return string.Empty;
                sb.Append((char) b);
            }

            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }
}
