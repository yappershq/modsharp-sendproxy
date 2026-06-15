using System.Text;

namespace YappersHQ.SendProxy.Native;

/// <summary>
///     Shared helpers for raw pointer reads in the engine send path.
/// </summary>
internal static unsafe class NativeUtil
{
    /// <summary>
    ///     Linux x64 user-space gate: valid heap/rodata pointers have bits [63:40] == 0x7F.
    ///     Cheap check to avoid dereferencing scalar field values and segfaulting.
    /// </summary>
    public static bool IsUserPtr(nint p) => p > 0 && ((ulong) p >> 40) == 0x7F;

    /// <summary>
    ///     Read up to <paramref name="maxLen"/> printable ASCII bytes from <paramref name="p"/>.
    ///     Returns <see cref="string.Empty"/> on any non-printable byte, NUL, or access exception.
    /// </summary>
    public static string ReadShortAscii(nint p, int maxLen)
    {
        if (!IsUserPtr(p))
            return string.Empty;
        try
        {
            var buf = stackalloc byte[maxLen + 1];
            var len = 0;
            for (; len < maxLen; len++)
            {
                var ch = *(byte*) (p + len);
                if (ch == 0) break;
                if (ch < 0x20 || ch > 0x7E) return string.Empty;
                buf[len] = ch;
            }
            buf[len] = 0;
            return len == 0 ? string.Empty
                : new string((sbyte*) buf, 0, len, System.Text.Encoding.ASCII);
        }
        catch { return string.Empty; }
    }

    /// <summary>
    ///     Read the field name from a CNetworkSerializerFieldInfo record.
    ///     Layout (confirmed by SerializerProbe): +0x08 = char* m_pszFieldName.
    /// </summary>
    public static string ReadFieldName(nint fieldInfo)
    {
        if (!IsUserPtr(fieldInfo))
            return string.Empty;
        try
        {
            var np = *(nint*) (fieldInfo + 0x08);
            return ReadShortAscii(np, 40);
        }
        catch { return string.Empty; }
    }
}
