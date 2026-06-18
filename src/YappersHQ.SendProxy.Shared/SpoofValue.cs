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
using System.Numerics;

namespace YappersHQ.SendProxy.Shared;

/// <summary>Tag that identifies which value family a <see cref="SpoofValue"/> carries.</summary>
public enum SpoofKind : byte
{
    Int,
    Float,
    Bool,
    Vector,
    String,
    Bytes,
}

/// <summary>
///     A discriminated-union value that carries any spoofable field value. The real value is exposed in
///     <see cref="ProxyContext.Original"/> when the callback fires; the callback produces a replacement via
///     <see cref="ProxyContext.SetAll"/> (every client sees the override) or
///     <see cref="ProxyContext.SetFor"/> (a specific client sees it). A <see cref="SpoofValue"/> is never
///     passed by <c>ref</c> to be mutated and returned — construct one with the static factories
///     (<see cref="Int"/>, <see cref="Float"/>, <see cref="Bool"/>, <see cref="Vector"/>,
///     <see cref="String"/>, <see cref="Bytes"/>) and pass it to <c>SetAll</c>/<c>SetFor</c>.
/// </summary>
public struct SpoofValue
{
    // int / uint / bool / fixed32 / fixed64 stored as raw int bits; float32 bits stored separately so
    // IEEE 754 identity is preserved without relying on unsafe reinterpret.
    private int     _intBits;
    private float   _float;
    private Vector3 _vec;
    private string? _str;
    private byte[]? _bytes;

    /// <summary>Identifies which backing field is active.</summary>
    public SpoofKind Kind { get; private set; }

    // -- Factories ---------------------------------------------------------------------------------

    /// <summary>Create a value carrying a signed-integer (or fixed / uint / bool in raw-bit form).</summary>
    public static SpoofValue Int(int v)
    {
        var sv = new SpoofValue();
        sv._intBits = v;
        sv.Kind     = SpoofKind.Int;

        return sv;
    }

    /// <summary>Create a value carrying a float32.</summary>
    public static SpoofValue Float(float v)
    {
        var sv = new SpoofValue();
        sv._float = v;
        sv.Kind   = SpoofKind.Float;

        return sv;
    }

    /// <summary>Create a value carrying a bool (stored as int bits 0/1).</summary>
    public static SpoofValue Bool(bool v)
    {
        var sv = new SpoofValue();
        sv._intBits = v ? 1 : 0;
        sv.Kind     = SpoofKind.Bool;

        return sv;
    }

    /// <summary>Create a value carrying a Vector3 / QAngle.</summary>
    public static SpoofValue Vector(Vector3 v)
    {
        var sv = new SpoofValue();
        sv._vec = v;
        sv.Kind = SpoofKind.Vector;

        return sv;
    }

    /// <summary>Create a value carrying a null-terminated string.</summary>
    public static SpoofValue String(string v)
    {
        var sv = new SpoofValue();
        sv._str = v;
        sv.Kind = SpoofKind.String;

        return sv;
    }

    /// <summary>Create a value carrying a raw byte array.</summary>
    public static SpoofValue Bytes(byte[] v)
    {
        var sv = new SpoofValue();
        sv._bytes = v;
        sv.Kind   = SpoofKind.Bytes;

        return sv;
    }

    // -- Typed accessors ---------------------------------------------------------------------------

    /// <summary>
    ///     Read or write the integer (or raw int-bit) value. Reading from a non-Int kind returns the raw
    ///     <c>_intBits</c> backing store; writing sets <see cref="Kind"/> to <see cref="SpoofKind.Int"/>.
    /// </summary>
    public int AsInt
    {
        get => _intBits;
        set { _intBits = value; Kind = SpoofKind.Int; }
    }

    /// <summary>
    ///     Read or write the float value. Reading from a non-Float kind returns 0f; writing sets
    ///     <see cref="Kind"/> to <see cref="SpoofKind.Float"/>.
    /// </summary>
    public float AsFloat
    {
        get => _float;
        set { _float = value; Kind = SpoofKind.Float; }
    }

    /// <summary>
    ///     Read or write the bool value (backed by <c>_intBits != 0</c>). Writing sets
    ///     <see cref="Kind"/> to <see cref="SpoofKind.Bool"/>.
    /// </summary>
    public bool AsBool
    {
        get => _intBits != 0;
        set { _intBits = value ? 1 : 0; Kind = SpoofKind.Bool; }
    }

    /// <summary>
    ///     Read or write the Vector3 value. Writing sets <see cref="Kind"/> to <see cref="SpoofKind.Vector"/>.
    /// </summary>
    public Vector3 AsVector
    {
        get => _vec;
        set { _vec = value; Kind = SpoofKind.Vector; }
    }

    /// <summary>
    ///     Read or write the string value. Writing sets <see cref="Kind"/> to <see cref="SpoofKind.String"/>.
    /// </summary>
    public string AsString
    {
        get => _str ?? string.Empty;
        set { _str = value; Kind = SpoofKind.String; }
    }

    /// <summary>
    ///     Read or write the byte-array value. Writing sets <see cref="Kind"/> to <see cref="SpoofKind.Bytes"/>.
    /// </summary>
    public byte[] AsBytes
    {
        get => _bytes ?? Array.Empty<byte>();
        set { _bytes = value; Kind = SpoofKind.Bytes; }
    }

    // -- Raw accessors used by the dispatch layer (FieldSubstitution) ------------------------------
    // These bypass Kind updates and expose the backing store directly so the dispatch can read the
    // exact bits to encode without re-boxing or re-interpreting. Prefer the typed As* accessors in
    // consumer code.

    /// <summary>Raw int bits — carries int / uint / bool / fixed32 / fixed64 in raw two's-complement form.</summary>
    public int     RawIntBits  { get => _intBits;  set => _intBits  = value; }
    /// <summary>Raw float32 — carries the float value independently of int-bit storage.</summary>
    public float   RawFloat    { get => _float;    set => _float    = value; }
    /// <summary>Raw Vector3 — carries the three float components.</summary>
    public Vector3 RawVec      { get => _vec;      set => _vec      = value; }
    /// <summary>Raw nullable string (null means empty/unset).</summary>
    public string? RawStr      { get => _str;      set => _str      = value; }
    /// <summary>Raw nullable byte array (null means empty/unset).</summary>
    public byte[]? RawBytes    { get => _bytes;    set => _bytes    = value; }
}
