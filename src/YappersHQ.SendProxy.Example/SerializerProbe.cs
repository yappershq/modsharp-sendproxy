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
using System.Text;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Managers;
using Sharp.Shared.Units;

namespace YappersHQ.SendProxy.Example;

/// <summary>
///     Read-only reverse-engineering diagnostic: resolve an entity's serializer via vtable slot 0
///     (GetNetworkSerializerInfo) and dump CNetworkSerializerClassInfo / field records to confirm
///     runtime offsets. Reads memory only — never writes. Lives in the example so the Core library
///     ships no commands and no diagnostic surface.
/// </summary>
internal static class SerializerProbe
{
    /// <summary>Walk entity indices, list the live ones + their class names, then full-Dump the first.</summary>
    public static unsafe void Scan(IEntityManager entityManager, ILogger logger)
    {
        var found      = 0;
        var firstValid = -1;
        for (var i = 0; i < 2048 && found < 24; i++)
        {
            var ent = entityManager.FindEntityByIndex((EntityIndex) i);
            if (ent is null)
            {
                continue;
            }

            // Gate every pointer before dereferencing — a bad vtable call would fault uncatchably, so
            // PtrLike is the protection (not a try/catch).
            var p = ent.GetAbsPtr();
            if (!PtrLike(p))
            {
                continue;
            }

            var vtbl = *(nint*) p;
            if (!PtrLike(vtbl))
            {
                continue;
            }

            var fn = *(nint*) vtbl;
            if (!PtrLike(fn))
            {
                continue;
            }

            var ci  = ((delegate* unmanaged<nint, nint>) fn)(p);
            var cls = PtrLike(ci) ? TryReadAscii(*(nint*) (ci + 0x08)) : "";
            logger.LogInformation("sp_scan idx={Idx} ptr=0x{P:X} class=\"{Cls}\"", i, p, cls);
            if (firstValid < 0)
            {
                firstValid = i;
            }

            found++;
        }

        logger.LogInformation("sp_scan: {Found} live entities (showing up to 24); dumping first valid idx={First}", found, firstValid);
        if (firstValid >= 0)
        {
            Dump(entityManager, logger, firstValid);
        }
    }

    public static unsafe void Dump(IEntityManager entityManager, ILogger logger, int entityIndex)
    {
        try
        {
            var ent = entityManager.FindEntityByIndex((EntityIndex) entityIndex);
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

            var vtbl      = *(nint*) entPtr;
            var slot0     = *(nint*) vtbl;
            var getInfo   = (delegate* unmanaged<nint, nint>) slot0;
            var classInfo = getInfo(entPtr);

            logger.LogInformation(
                "sp_dump ent={Index}: ptr=0x{Ptr:X} vtbl=0x{Vtbl:X} slot0=0x{Slot:X} classInfo=0x{Class:X}",
                entityIndex, entPtr, vtbl, slot0, classInfo);

            if (classInfo == 0)
            {
                return;
            }

            var className  = TryReadAscii(*(nint*) (classInfo + 0x08));
            var fieldCount = *(int*) (classInfo + 0x10);
            var fieldArray = *(nint*) (classInfo + 0x18);
            logger.LogInformation("  class=\"{Cls}\" fieldCount={Cnt} fieldArray=0x{Arr:X}",
                className, fieldCount, fieldArray);

            if (fieldArray == 0 || fieldCount <= 0 || fieldCount > 4096)
            {
                return;
            }

            // Only read field[0] — speculative multi-field walks hit unmapped pages (uncatchable AV).
            var rec0 = *(nint*) (fieldArray + 0);
            if (rec0 != 0)
            {
                var nameHash = *(uint*) (rec0 + 0x00);
                var name     = TryReadAscii(*(nint*) (rec0 + 0x08));
                logger.LogInformation("  field[0] rec=0x{Rec:X} nameHash=0x{H:X} name=\"{Name}\"", rec0, nameHash, name);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "sp_dump failed for entity {Index}", entityIndex);
        }
    }

    /// <summary>
    ///     Find the first live entity with serializer class <paramref name="classFilter"/>, walk its field
    ///     array for <paramref name="fieldName"/>, and dump the raw qword window (offsets 0x20..0x50) with
    ///     pointer-likeness tags. No writes; all derefs are range-gated.
    /// </summary>
    public static unsafe void DumpField(IEntityManager entityManager, ILogger logger, string classFilter, string fieldName)
    {
        try
        {
            for (var i = 0; i < 2048; i++)
            {
                var ent = entityManager.FindEntityByIndex((EntityIndex) i);
                if (ent is null)
                {
                    continue;
                }

                var p = ent.GetAbsPtr();
                if (p == 0)
                {
                    continue;
                }

                var ci = ((delegate* unmanaged<nint, nint>) (*(nint*) (*(nint*) p)))(p);
                if (ci == 0)
                {
                    continue;
                }

                if (TryReadAscii(*(nint*) (ci + 0x08)) != classFilter)
                {
                    continue;
                }

                var count = *(int*) (ci + 0x10);
                var arr   = *(nint*) (ci + 0x18);
                if (arr == 0 || count <= 0 || count > 4096)
                {
                    continue;
                }

                logger.LogInformation("sp_field: matched class \"{Cls}\" at idx={Idx} (fields={Cnt})",
                    classFilter, i, count);

                if (fieldName == "*")
                {
                    for (var f = 0; f < count; f++)
                    {
                        var r = *(nint*) (arr + f * 8);
                        if (!PtrLike(r))
                        {
                            continue;
                        }

                        logger.LogInformation("  field[{F}] = \"{Name}\"", f, TryReadAscii(*(nint*) (r + 0x08)));
                    }

                    return;
                }

                for (var f = 0; f < count; f++)
                {
                    var rec = *(nint*) (arr + f * 8);
                    if (!PtrLike(rec))
                    {
                        continue;
                    }

                    if (TryReadAscii(*(nint*) (rec + 0x08)) != fieldName)
                    {
                        continue;
                    }

                    logger.LogInformation("sp_field: FOUND \"{Cls}::{Field}\" rec=0x{Rec:X}", classFilter, fieldName, rec);
                    for (var off = 0x20; off <= 0x50; off += 0x08)
                    {
                        var q   = *(ulong*) (rec + off);
                        var ptr = (nint) q;
                        var tag = PtrLike(ptr) ? "ptr?" : (q < 0x100000 ? "int?" : "----");

                        var deref = "";
                        if (PtrLike(ptr))
                        {
                            var v = *(ulong*) ptr;
                            if (PtrLike((nint) v))
                            {
                                var s0 = *(ulong*) (nint) v;
                                deref = $" -> *=0x{v:X}" + (PtrLike((nint) s0) ? $" -> **=0x{s0:X} (code?)" : "");
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

    // Linux x64 heap/rodata heuristic: high bytes == 0x00007F. PtrLike also requires > 0x10000 to
    // exclude low-address constants.
    private static bool PtrLike(nint p)
        => p > 0x10000 && ((ulong) p >> 40) == 0x7F;

    private static unsafe string TryReadAscii(nint p)
    {
        if (p <= 0 || ((ulong) p >> 40) != 0x7F)
        {
            return string.Empty;
        }

        try
        {
            var sb = new StringBuilder();
            for (var i = 0; i < 31; i++)
            {
                var b = *(byte*) (p + i);
                if (b == 0)
                {
                    break;
                }

                if (b < 0x20 || b > 0x7E)
                {
                    return string.Empty;
                }

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
