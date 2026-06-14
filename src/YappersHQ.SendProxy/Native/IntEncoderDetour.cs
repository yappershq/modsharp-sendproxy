using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Hooks;

namespace YappersHQ.SendProxy.Native;

/// <summary>
///     READ-ONLY probe detour on CFlattenedSerializer::EncodeInt32. The int32 encoder is a leaf-ish
///     5-register-arg SysV function (rdi=bf_write*, rsi=fieldInfo*, rdx, rcx, r8d) with NO stack
///     args, so a 5-arg cdecl passthrough trampoline is safe and lossless.
///
///     Goal: identify which of {rdx, rcx, r8d} carries the spoofable integer value for m_iHealth.
///     m_iHealth is detected by fieldInfo+0x40 (m_nFieldOffset) == 728 (0x2D8).
///
///     The first 12 qualifying hits log all 5 arg registers + dereferenced int32 at rdx and rcx,
///     so we can read off which pointer holds the live field value. No value is modified.
/// </summary>
internal static unsafe class IntEncoderDetour
{
    private static IDetourHook? _hook;
    private static nint _trampoline;
    private static ILogger? _logger;
    private static int _count;

    public static bool Install(InterfaceBridge bridge, ILogger logger, nint intEncoderAddr)
    {
        if (_hook is not null)
        {
            logger.LogInformation("IntEncoder detour already installed");
            return true;
        }
        if (intEncoderAddr == 0)
        {
            logger.LogWarning("IntEncoder detour: null target address");
            return false;
        }

        _logger = logger;
        _count = 0;

        var hook = bridge.HookManager.CreateDetourHook();
        var hookFn = (nint) (delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint>) &Hook;
        hook.Prepare(intEncoderAddr, hookFn);
        if (!hook.Install())
        {
            logger.LogWarning("IntEncoder detour: Install() failed");
            return false;
        }

        _hook = hook;
        _trampoline = hook.Trampoline;
        logger.LogInformation("IntEncoder detour installed @ 0x{Addr:X} (trampoline=0x{Tr:X})",
            intEncoderAddr, _trampoline);
        return true;
    }

    public static void Uninstall()
    {
        _hook?.Uninstall();
        _hook?.Dispose();
        _hook = null;
        _trampoline = 0;
        _logger?.LogInformation("IntEncoder detour uninstalled");
    }

    // SysV calling convention: rdi=a, rsi=b, rdx=c, rcx=d, r8d=e.
    // 5 register args, no stack args — safe passthrough.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static nint Hook(nint a, nint b, nint c, nint d, nint e)
    {
        try
        {
            // fieldInfo (b/rsi) must look like a valid heap pointer.
            var fieldOff = -1;
            if (IsUserPtr(b))
            {
                try { fieldOff = *(int*) (b + 0x40); } catch { /* guard */ }
            }

            if (fieldOff == 728)
            {
                var n = Interlocked.Increment(ref _count);
                if (n <= 12 && _logger is { } log)
                {
                    var valC = 0;
                    var valD = 0;
                    try { if (IsUserPtr(c)) valC = *(int*) c; } catch { /* guard */ }
                    try { if (IsUserPtr(d)) valD = *(int*) d; } catch { /* guard */ }

                    log.LogInformation(
                        "INTENC#{N} off={Off} a=0x{A:X} b=0x{B:X} c=0x{C:X} d=0x{D:X} e=0x{E:X} *(int*)c={ValC} *(int*)d={ValD}",
                        n, fieldOff, a, b, c, d, e, valC, valD);
                }
            }
        }
        catch { /* never throw out of an unmanaged callback */ }

        // Always call the original unchanged — pure passthrough.
        return ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint>) _trampoline)(a, b, c, d, e);
    }

    // Cheap range gate: Linux user-space mapped pages sit in [0x000000000000, 0x7fffffffffff].
    // The top byte of any valid user pointer has bits [63:48] == 0 and bit 47 == 0 (canonical),
    // so (ulong)p >> 40 == 0x7F is a reliable "looks like user heap/stack" heuristic.
    private static bool IsUserPtr(nint p) => p > 0 && ((ulong) p >> 40) == 0x7F;
}
