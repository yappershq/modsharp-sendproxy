using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Hooks;

namespace YappersHQ.SendProxy.Native;

/// <summary>
///     Live value-spoof detour on CFlattenedSerializer::EncodeInt32. ABI (SysV x86-64):
///     <c>enc(rdi=bf_write, rsi=fieldInfo, rdx, rcx, r8d)</c> → Hook(a,b,c,d,e).
///
///     The encoder's first instruction is <c>mov (%rcx),%rax</c> so it reads the value from
///     the pointer in rcx (= arg d). To spoof a field: write the fake value into a native scratch
///     int and redirect d to point at it. The encoder reads the fake; the real entity memory is
///     never modified → clients see the spoofed value, server keeps the real one.
///
///     Field identity: <c>*(int*)(fieldInfo+0x40)</c> is m_nFieldOffset (e.g. 728 for m_iHealth).
/// </summary>
internal static unsafe class IntEncoderDetour
{
    private static IDetourHook? _hook;
    private static nint _trampoline;
    private static ILogger? _logger;

    // field byte-offset → fake value to broadcast
    private static readonly Dictionary<int, int> _spoofs = new();
    // native scratch int for the redirected pointer; allocated on Install, freed on Uninstall
    private static nint _scratch;

    // ── Public spoof API ──────────────────────────────────────────────────────────────────────

    public static void SetSpoof(int fieldOffset, int fakeValue) => _spoofs[fieldOffset] = fakeValue;

    public static void ClearSpoof(int fieldOffset) => _spoofs.Remove(fieldOffset);

    public static void ClearAll() => _spoofs.Clear();

    public static bool HasSpoofs => _spoofs.Count > 0;

    // ── Lifecycle ─────────────────────────────────────────────────────────────────────────────

    public static bool Install(InterfaceBridge bridge, ILogger logger, nint intEncoderAddr)
    {
        if (_hook is not null)
            return true; // idempotent

        if (intEncoderAddr == 0)
        {
            logger.LogWarning("IntEncoder detour: null target address");
            return false;
        }

        _logger = logger;

        if (_scratch == 0)
            _scratch = (nint) NativeMemory.Alloc(sizeof(int));

        var hook = bridge.HookManager.CreateDetourHook();
        var hookFn = (nint) (delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint>) &Hook;
        hook.Prepare(intEncoderAddr, hookFn);
        if (!hook.Install())
        {
            logger.LogWarning("IntEncoder detour: Install() failed");
            NativeMemory.Free((void*) _scratch);
            _scratch = 0;
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

        if (_scratch != 0)
        {
            NativeMemory.Free((void*) _scratch);
            _scratch = 0;
        }

        _logger?.LogInformation("IntEncoder detour uninstalled");
        _logger = null;
    }

    // ── Hook ─────────────────────────────────────────────────────────────────────────────────

    // SysV calling convention: rdi=a, rsi=b, rdx=c, rcx=d, r8d=e.
    // fieldInfo = b; value ptr = d (encoder reads *(int*)d).
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static nint Hook(nint a, nint b, nint c, nint d, nint e)
    {
        try
        {
            if (IsUserPtr(b))
            {
                int off = *(int*) (b + 0x40);
                if (_spoofs.TryGetValue(off, out var fake))
                {
                    *(int*) _scratch = fake;
                    d = _scratch;
                }
            }
        }
        catch { /* never throw out of an unmanaged callback */ }

        return ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint>) _trampoline)(a, b, c, d, e);
    }

    // Cheap user-space range gate: valid Linux user pointers have bits [63:48] == 0,
    // and the 7th byte == 0x7F is a reliable heap/stack heuristic.
    private static bool IsUserPtr(nint p) => p > 0 && ((ulong) p >> 40) == 0x7F;
}
