using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using YappersHQ.SendProxy.Native;
using YappersHQ.SendProxy.Shared;

namespace YappersHQ.SendProxy;

/// <summary>
///     Hook bookkeeping and the public <see cref="ISendProxyManager"/> implementation.
///     Phase-1 hooks (HookInt/HookFloat/etc.) are recorded in the registry and drive
///     <see cref="ApplyEncoderSwap"/> once the Phase-1 encoder swap is implemented.
///     Phase-2 per-client hooks use <see cref="FieldSubstitution"/> directly.
/// </summary>
internal sealed class SendProxyManager : ISendProxyManager
{
    private const int GameRulesEntity = -1;

    private readonly ILogger _logger;
    private readonly Dictionary<(int Entity, string Prop), List<HookEntry>> _hooks = new();
    private readonly Dictionary<(int Entity, string Prop), List<PropChangeCallback>> _changeHooks = new();

    // Injected by SendProxyModule.PostInit — installs Phase-2 sub-detours (idempotent).
    // Called lazily when the first per-client callback is registered.
    private Func<bool>? _ensureSubDetours;

    public SendProxyManager(ILogger logger) => _logger = logger;

    internal void SetSubDetourInstaller(Func<bool> installer) => _ensureSubDetours = installer;

    private sealed record HookEntry(SendPropType Type, Delegate Callback, int Element);

    private bool AddHook(int entity, string prop, SendPropType type, Delegate cb, int element)
    {
        if (string.IsNullOrEmpty(prop) || cb is null)
            return false;

        var key = (entity, prop);
        if (!_hooks.TryGetValue(key, out var list))
            _hooks[key] = list = [];
        list.Add(new HookEntry(type, cb, element));

        ApplyEncoderSwap(entity, prop, type, element);
        return true;
    }

    public bool HookInt(int entity, string prop, SendProxyIntCallback callback)
        => AddHook(entity, prop, SendPropType.Int, callback, 0);

    public bool HookFloat(int entity, string prop, SendProxyFloatCallback callback)
        => AddHook(entity, prop, SendPropType.Float, callback, 0);

    public bool HookString(int entity, string prop, SendProxyStringCallback callback)
        => AddHook(entity, prop, SendPropType.String, callback, 0);

    public bool HookVector(int entity, string prop, SendProxyVectorCallback callback)
        => AddHook(entity, prop, SendPropType.Vector, callback, 0);

    public bool HookArrayInt(int entity, string prop, int element, SendProxyIntCallback callback)
        => AddHook(entity, prop, SendPropType.Int, callback, element);

    public bool HookArrayFloat(int entity, string prop, int element, SendProxyFloatCallback callback)
        => AddHook(entity, prop, SendPropType.Float, callback, element);

    public bool HookGameRulesInt(string prop, SendProxyIntCallback callback)
        => AddHook(GameRulesEntity, prop, SendPropType.Int, callback, 0);

    public bool HookGameRulesFloat(string prop, SendProxyFloatCallback callback)
        => AddHook(GameRulesEntity, prop, SendPropType.Float, callback, 0);

    public bool Unhook(int entity, string prop, Delegate callback)
    {
        if (!_hooks.TryGetValue((entity, prop), out var list))
            return false;
        var removed = list.RemoveAll(h => h.Callback == callback) > 0;
        if (list.Count == 0)
        {
            _hooks.Remove((entity, prop));
            RemoveEncoderSwap(entity, prop);
        }
        return removed;
    }

    public bool Unhook(int entity, string prop)
    {
        var had = _hooks.Remove((entity, prop));
        if (had) RemoveEncoderSwap(entity, prop);
        return had;
    }

    public bool UnhookGameRules(string prop, Delegate callback) => Unhook(GameRulesEntity, prop, callback);

    /// <summary>
    ///     Drop every hook bound to <paramref name="entity"/>. Called from IEntityListener.OnEntityDeleted —
    ///     entity indices are reused after disconnect/round restart so stale hooks must not persist.
    /// </summary>
    internal void RemoveEntityHooks(int entity)
    {
        foreach (var key in _hooks.Keys.Where(k => k.Entity == entity).ToList())
        {
            _hooks.Remove(key);
            RemoveEncoderSwap(key.Entity, key.Prop);
        }

        foreach (var key in _changeHooks.Keys.Where(k => k.Entity == entity).ToList())
            _changeHooks.Remove(key);
    }

    public bool IsHooked(int entity, string prop) => _hooks.ContainsKey((entity, prop));

    public bool IsHookedGameRules(string prop) => _hooks.ContainsKey((GameRulesEntity, prop));

    public bool HookPropChange(int entity, string prop, PropChangeCallback callback)
    {
        var key = (entity, prop);
        if (!_changeHooks.TryGetValue(key, out var list))
            _changeHooks[key] = list = [];
        list.Add(callback);
        return true;
    }

    public bool UnhookPropChange(int entity, string prop, PropChangeCallback callback)
        => _changeHooks.TryGetValue((entity, prop), out var list) && list.Remove(callback);

    // ── Phase-1 encoder swap (TODO: implement once EncodeField dispatch is settled) ──
    //
    //  The live field record layout (verified by SerializerProbe) is:
    //    +0x00 m_FieldNameHash, +0x08 m_pszFieldName*, +0x38 m_nFieldSize/m_nFieldOffset
    //  There is no stored per-field encode-fn pointer in this record to swap; the encoder
    //  is resolved from the named m_NetworkEncoder at serialize time. The substitution
    //  mechanism is WFL-level (Phase-2). Phase-1 ApplyEncoderSwap is a no-op placeholder.
    private static void ApplyEncoderSwap(int entity, string prop, SendPropType type, int element)
    {
        // TODO(phase1): install EncodeField detour + field fast-filter when mechanism is settled.
    }

    private static void RemoveEncoderSwap(int entity, string prop)
    {
        // TODO(phase1): restore original encoder pointer.
    }

    // ── Phase-2 per-client raw callback API ──────────────────────────────────

    /// <inheritdoc/>
    public void HookInt(string serializerName, string fieldName, PerClientIntProxy callback)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName) || callback is null)
            return;

        if (_ensureSubDetours is { } installer)
        {
            if (!installer())
            {
                _logger.LogWarning(
                    "SendProxy: HookInt(ser, field, callback) — Phase-2 sub-detours failed; "
                    + "callback for \"{Ser}::{Field}\" NOT registered",
                    serializerName, fieldName);
                return;
            }
        }
        else
        {
            _logger.LogWarning(
                "SendProxy: HookInt(ser, field, callback) — Phase-2 installer not wired yet; "
                + "callback for \"{Ser}::{Field}\" NOT registered",
                serializerName, fieldName);
            return;
        }

        FieldSubstitution.SetCallback(serializerName, fieldName, callback);
        FieldSubstitution.Mode = SubstitutionMode.Fake;

        _logger.LogInformation(
            "SendProxy: per-client callback registered for \"{Ser}::{Field}\"",
            serializerName, fieldName);
    }

    /// <inheritdoc/>
    public void UnhookInt(string serializerName, string fieldName)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName))
            return;

        FieldSubstitution.ClearCallback(serializerName, fieldName);
        _logger.LogInformation(
            "SendProxy: per-client callback removed for \"{Ser}::{Field}\"",
            serializerName, fieldName);
    }

    /// <inheritdoc/>
    public void UnhookAllPerClient()
    {
        FieldSubstitution.ClearCallbacks();
        if (!FieldSubstitution.HasSpoofs)
            FieldSubstitution.Uninstall();
        _logger.LogInformation("SendProxy: all per-client callbacks removed");
    }

    internal void Clear()
    {
        _hooks.Clear();
        _changeHooks.Clear();
    }
}
