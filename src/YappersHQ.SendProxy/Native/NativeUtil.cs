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

namespace YappersHQ.SendProxy.Native;

internal static unsafe class NativeUtil
{
    // Safety boundary for every raw dereference, by platform canonical user-space shape:
    //   Linux x64   : user pointers are 0x00007Fxx_xxxxxxxx → bits [63:40] == 0x7F (byte-identical to before).
    //   Windows x64 : user pointers are below 0x0000_8000_0000_0000 → bits [63:48] == 0 (and non-tiny).
    // The Windows branch makes the gate correct if the module runs on a Windows server (the old Linux-only
    // shape would mis-classify Windows pointers — silently breaking every guard there).
    private static readonly bool IsWindows = OperatingSystem.IsWindows();

    /// <summary>
    ///     User-space pointer gate (platform-aware). Cheap check to avoid dereferencing a scalar field value
    ///     and segfaulting on a non-pointer.
    /// </summary>
    public static bool IsUserPtr(nint p)
        => IsWindows
            ? p > 0x10000 && ((ulong) p >> 48) == 0
            : p > 0 && ((ulong) p >> 40) == 0x7F;

    /// <summary>
    ///     Read up to <paramref name="maxLen"/> printable ASCII bytes from <paramref name="p"/>. Returns
    ///     <see cref="string.Empty"/> on a NUL/non-printable byte or a non-user pointer. The IsUserPtr gate
    ///     is the safety boundary — a bad dereference faults the process (uncatchable), so there is no
    ///     try/catch here.
    /// </summary>
    public static string ReadShortAscii(nint p, int maxLen)
    {
        if (!IsUserPtr(p))
        {
            return string.Empty;
        }

        var buf = stackalloc byte[maxLen + 1];
        var len = 0;
        for (; len < maxLen; len++)
        {
            var ch = *(byte*) (p + len);
            if (ch == 0)
            {
                break;
            }

            if (ch < 0x20 || ch > 0x7E)
            {
                return string.Empty;
            }

            buf[len] = ch;
        }

        buf[len] = 0;

        return len == 0
            ? string.Empty
            : new string((sbyte*) buf, 0, len, Encoding.ASCII);
    }

    /// <summary>
    ///     Read the field name from a CNetworkSerializerFieldInfo record (+0x08 = char* m_pszFieldName).
    /// </summary>
    public static string ReadFieldName(nint fieldInfo)
        => IsUserPtr(fieldInfo) ? ReadShortAscii(*(nint*) (fieldInfo + 0x08), 40) : string.Empty;
}
