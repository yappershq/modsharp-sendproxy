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

namespace YappersHQ.SendProxy.Native;

internal static unsafe class NativeUtil
{
    // Safety boundary for every raw dereference, by platform canonical user-space shape:
    //   Linux x64   : user pointers are 0x00007Fxx_xxxxxxxx → bits [63:40] == 0x7F.
    //   Windows x64 : user pointers are below 0x0000_8000_0000_0000 → bits [63:48] == 0 (and non-tiny).
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
    ///     Compare a NUL-terminated engine name <paramref name="charPtr"/> to a UTF8 byte pattern WITHOUT
    ///     allocating a managed string. Exact match = every byte equal AND the terminator falls right after
    ///     (same length). The encode/send paths match field/serializer names by pointer/byte-compare instead
    ///     of building a string per field per recipient, so SendProxy never allocates a string at runtime.
    /// </summary>
    public static bool NameEquals(nint charPtr, byte[] utf8)
    {
        if (!IsUserPtr(charPtr))
        {
            return false;
        }

        var p = (byte*) charPtr;
        for (var i = 0; i < utf8.Length; i++)
        {
            if (p[i] != utf8[i])
            {
                return false;
            }
        }

        return p[utf8.Length] == 0;
    }
}
