using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Hooks;

namespace YappersHQ.SendProxy.Native;

/// <summary>
///     READ-ONLY probe detour on CFlattenedSerializer::EncodeField. Captures the 6 SysV argument
///     registers (rdi..r9) + return register, logs the first N calls (to identify which arg is the
///     field record), then calls the original unchanged. Pure passthrough — preserves all args +
///     the return value — so it's safe for any function with ≤6 integer/pointer args regardless of
///     its exact arity. This both proves the IDetourHook infra and reveals EncodeField's real args
///     live (its decompiled signature was from a wrong entry). Value substitution comes after.
/// </summary>
internal static unsafe class EncodeFieldDetour
{
    private static IDetourHook? _hook;
    private static nint _trampoline;
    private static ILogger? _logger;
    private static int _count;

    public static bool Install(InterfaceBridge bridge, ILogger logger, nint encodeFieldAddr)
    {
        if (_hook is not null)
        {
            logger.LogInformation("EncodeField detour already installed");
            return true;
        }
        if (encodeFieldAddr == 0)
        {
            logger.LogWarning("EncodeField detour: null target address");
            return false;
        }

        _logger = logger;
        _count = 0;

        var hook = bridge.HookManager.CreateDetourHook();
        var hookFn = (nint) (delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint>) &Hook;
        hook.Prepare(encodeFieldAddr, hookFn);
        if (!hook.Install())
        {
            logger.LogWarning("EncodeField detour: Install() failed");
            return false;
        }

        _hook = hook;
        _trampoline = hook.Trampoline;
        logger.LogInformation("EncodeField detour installed @ 0x{Addr:X} (trampoline=0x{Tr:X})",
            encodeFieldAddr, _trampoline);
        return true;
    }

    public static void Uninstall()
    {
        _hook?.Uninstall();
        _hook?.Dispose();
        _hook = null;
        _trampoline = 0;
        _logger?.LogInformation("EncodeField detour uninstalled");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static nint Hook(nint a, nint b, nint c, nint d, nint e, nint f)
    {
        var n = Interlocked.Increment(ref _count);
        if (n <= 24 && _logger is { } log)
        {
            try
            {
                log.LogInformation(
                    "EF#{N} a=0x{A:X}[{NA}] b=0x{B:X}[{NB}] c=0x{C:X}[{NC}] d=0x{D:X} e=0x{E:X} f=0x{F:X}",
                    n, a, NameAt(a), b, NameAt(b), c, NameAt(c), d, e, f);
            }
            catch { /* never let logging break the passthrough */ }
        }

        // Call the original unchanged (preserves args + return value).
        return ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint>) _trampoline)(a, b, c, d, e, f);
    }

    // If `p` looks like a field record, *(p+0x08) is m_pszFieldName (char*). Probe it as ASCII.
    private static string NameAt(nint p)
    {
        if (p <= 0 || ((ulong) p >> 40) != 0x7F)
            return string.Empty;
        try
        {
            var namePtr = *(nint*) (p + 0x08);
            if (namePtr <= 0 || ((ulong) namePtr >> 40) != 0x7F)
                return string.Empty;
            var bytes = new System.Text.StringBuilder();
            for (var i = 0; i < 24; i++)
            {
                var ch = *(byte*) (namePtr + i);
                if (ch == 0) break;
                if (ch < 0x20 || ch > 0x7E) return string.Empty;
                bytes.Append((char) ch);
            }
            return bytes.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }
}
