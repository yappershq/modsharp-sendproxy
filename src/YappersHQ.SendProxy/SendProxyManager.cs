using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using YappersHQ.SendProxy.Native;
using YappersHQ.SendProxy.Shared;

namespace YappersHQ.SendProxy;

/// <summary>
///     Hook bookkeeping + the public <see cref="ISendProxyManager"/> surface. Registration is
///     fully implemented; the live per-field encoder swap is applied by <see cref="EncoderHook"/>
///     once verified (see <see cref="FlattenedSerializerLayout"/>). Until then hooks are recorded
///     and surfaced via the API, but values are not yet substituted on the wire.
/// </summary>
internal sealed class SendProxyManager : ISendProxyManager
{
    private const int GameRulesEntity = -1;

    private readonly ILogger _logger;
    private readonly Dictionary<(int Entity, string Prop), List<HookEntry>> _hooks = new();
    private readonly Dictionary<(int Entity, string Prop), List<PropChangeCallback>> _changeHooks = new();

    public SendProxyManager(ILogger logger) => _logger = logger;

    private sealed record HookEntry(SendPropType Type, Delegate Callback, int Element);

    private bool AddHook(int entity, string prop, SendPropType type, Delegate cb, int element)
    {
        if (string.IsNullOrEmpty(prop) || cb is null)
            return false;

        var key = (entity, prop);
        if (!_hooks.TryGetValue(key, out var list))
            _hooks[key] = list = [];
        list.Add(new HookEntry(type, cb, element));

        if (!EncoderHook.Enabled)
            _logger.LogWarning(
                "SendProxy hook registered for {Entity}/{Prop} ({Type}) but the live encoder patch is "
                + "DISABLED (offsets unverified) — value will NOT be substituted yet. See README.",
                entity, prop, type);
        else
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
            if (EncoderHook.Enabled)
                RemoveEncoderSwap(entity, prop);
        }
        return removed;
    }

    public bool UnhookGameRules(string prop, Delegate callback) => Unhook(GameRulesEntity, prop, callback);

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

    // ── Live encoder swap (Phase 1 completion — gated by EncoderHook.Enabled) ────────
    // Resolve the CNetworkSerializerFieldInfo for (entity-class, prop), read the encoder
    // dispatch pointer at +FlattenedSerializerLayout.EncoderDispatchOffset, and swap vtable
    // slot 0 for our trampoline (which invokes the recorded callback). Implemented once the
    // offsets are verified on a live server; see README "Phase 1 — remaining".
    private void ApplyEncoderSwap(int entity, string prop, SendPropType type, int element)
    {
        // TODO(phase1-live): walk entity -> flattened serializer -> field -> encoder dispatch,
        // install trampoline. No-op until verified to avoid corrupting engine memory.
    }

    private void RemoveEncoderSwap(int entity, string prop)
    {
        // TODO(phase1-live): restore the original encoder pointer.
    }

    internal void Clear()
    {
        _hooks.Clear();
        _changeHooks.Clear();
    }
}
