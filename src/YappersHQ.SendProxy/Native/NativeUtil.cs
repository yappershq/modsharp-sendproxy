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

using System.Text;

namespace YappersHQ.SendProxy.Native;

internal static unsafe class NativeUtil
{
    /// <summary>
    ///     Linux x64 user-space gate: valid heap/rodata pointers have bits [63:40] == 0x7F. Cheap check
    ///     to avoid dereferencing scalar field values and segfaulting.
    /// </summary>
    public static bool IsUserPtr(nint p)
        => p > 0 && ((ulong) p >> 40) == 0x7F;

    /// <summary>
    ///     Read up to <paramref name="maxLen"/> printable ASCII bytes from <paramref name="p"/>.
    ///     Returns <see cref="string.Empty"/> on any non-printable byte, NUL, or access exception.
    /// </summary>
    public static string ReadShortAscii(nint p, int maxLen)
    {
        if (!IsUserPtr(p))
        {
            return string.Empty;
        }

        try
        {
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
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    ///     Read the field name from a CNetworkSerializerFieldInfo record (+0x08 = char* m_pszFieldName).
    /// </summary>
    public static string ReadFieldName(nint fieldInfo)
    {
        if (!IsUserPtr(fieldInfo))
        {
            return string.Empty;
        }

        try
        {
            var np = *(nint*) (fieldInfo + 0x08);

            return ReadShortAscii(np, 40);
        }
        catch
        {
            return string.Empty;
        }
    }
}
