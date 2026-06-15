using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Hooks;

namespace YappersHQ.SendProxy.Native;

/// <summary>
///     Live value-spoof detour on CFlattenedSerializer::EncodeInt32.
///     ABI: rdi=bf_write, rsi=fieldInfo, rdx, rcx=value-ptr, r8d → Hook(a,b,c,d,e).
///     The encoder reads *(int*)rcx for the value. To spoof: write the fake value into a native
///     scratch int and redirect d to point at it. The real entity memory is never modified.
///     Field identity: *(nint*)(fieldInfo+0x08) = char* m_pszFieldName (confirmed by SerializerProbe).
/// </summary>
internal static unsafe class IntEncoderDetour
{
    private static IDetourHook? _hook;
    private static nint _trampoline;
    private static ILogger? _logger;

    // Field name → fake value.
    private static readonly Dictionary<string, int> _spoofs = new(StringComparer.Ordinal);
    // Native scratch int for the redirected value pointer.
    private static nint _scratch;

    // ── Public API ────────────────────────────────────────────────────────────

    public static void SetSpoof(string name, int fakeValue) => _spoofs[name] = fakeValue;
    public static void ClearSpoof(string name)              => _spoofs.Remove(name);
    public static void ClearAll()                           => _spoofs.Clear();
    public static bool HasSpoofs                            => _spoofs.Count > 0;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

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

    // ── Hook ─────────────────────────────────────────────────────────────────

    // SysV: rdi=a, rsi=b (fieldInfo), rdx=c, rcx=d (value ptr), r8d=e.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static nint Hook(nint a, nint b, nint c, nint d, nint e)
    {
        try
        {
            if (NativeUtil.IsUserPtr(b))
            {
                var name = NativeUtil.ReadFieldName(b);
                if (name.Length > 0 && _spoofs.TryGetValue(name, out var fake))
                {
                    *(int*) _scratch = fake;
                    d = _scratch;
                }
            }
        }
        catch { }

        return ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint>) _trampoline)(a, b, c, d, e);
    }
}
