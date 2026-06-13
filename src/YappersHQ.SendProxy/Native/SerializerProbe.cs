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

            // Dump the first few field records: their head bytes + ascii (find m_pszFieldName)
            // and the +0x38 encoder dispatch ptr (and its slot 0 = encode fn).
            var dump = Math.Min(fieldCount, 3);
            for (var fi = 0; fi < dump; fi++)
            {
                var rec = *(nint*) (fieldArray + fi * 8);
                if (rec == 0) continue;
                logger.LogInformation("  --- field[{Fi}] rec=0x{Rec:X} ---", fi, rec);
                for (var off = 0; off < 0x50; off += 8)
                {
                    var q = *(nint*) (rec + off);
                    var s = TryReadAscii(q);
                    logger.LogInformation("    rec+0x{Off:X2} = 0x{Q:X}{Str}",
                        off, q, s.Length > 0 ? $"  \"{s}\"" : string.Empty);
                }

                var encoder = *(nint*) (rec + 0x38);
                var encodeFn = encoder != 0 ? *(nint*) encoder : 0;
                logger.LogInformation("    rec+0x38 encoder=0x{Enc:X} slot0(encodeFn)=0x{Fn:X}", encoder, encodeFn);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "sp_dump failed for entity {Index}", entityIndex);
        }
    }

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
