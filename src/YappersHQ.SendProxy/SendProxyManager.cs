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

    /// <summary>Shared installer check — ensures Phase-2 sub-detours are up before registering anything.</summary>
    private bool EnsureDetours(string context)
    {
        if (_ensureSubDetours is { } installer)
        {
            if (!installer()) { _logger.LogWarning("SendProxy: {Ctx} — Phase-2 sub-detours failed", context); return false; }
            return true;
        }
        _logger.LogWarning("SendProxy: {Ctx} — Phase-2 installer not wired yet", context);
        return false;
    }

    /// <inheritdoc/>
    public void HookInt(string serializerName, string fieldName, PerClientIntProxy callback)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName) || callback is null)
            return;

        if (!EnsureDetours($"HookInt(\"{serializerName}::{fieldName}\")"))
            return;

        FieldSubstitution.SetCallback(serializerName, fieldName, callback);
        FieldSubstitution.Mode = SubstitutionMode.Fake;

        _logger.LogInformation(
            "SendProxy: per-client callback (all entities) registered for \"{Ser}::{Field}\"",
            serializerName, fieldName);
    }

    /// <inheritdoc/>
    public void HookEntityInt(int entityIndex, string serializerName, string fieldName, PerClientIntProxy callback)
    {
        if (entityIndex < 0) { _logger.LogWarning("SendProxy: HookEntityInt — entityIndex must be >= 0 (got {Idx}); use HookInt for all-entity scope", entityIndex); return; }
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName) || callback is null)
            return;

        if (!EnsureDetours($"HookEntityInt(ent={entityIndex}, \"{serializerName}::{fieldName}\")"))
            return;

        FieldSubstitution.SetEntityCallback(entityIndex, serializerName, fieldName, callback);
        FieldSubstitution.Mode = SubstitutionMode.Fake;

        _logger.LogInformation(
            "SendProxy: per-client callback registered for ent={Ent} \"{Ser}::{Field}\"",
            entityIndex, serializerName, fieldName);
    }

    /// <inheritdoc/>
    public void SetEntitySpoof(int entityIndex, string serializerName, string fieldName, int value)
    {
        if (entityIndex < 0) { _logger.LogWarning("SendProxy: SetEntitySpoof — entityIndex must be >= 0 (got {Idx})", entityIndex); return; }
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName))
            return;

        if (!EnsureDetours($"SetEntitySpoof(ent={entityIndex}, \"{serializerName}::{fieldName}\")"))
            return;

        FieldSubstitution.SetEntitySpoof(entityIndex, serializerName, fieldName, value);
        FieldSubstitution.Mode = SubstitutionMode.Fake;

        _logger.LogInformation(
            "SendProxy: entity spoof registered ent={Ent} \"{Ser}::{Field}\" → {Value}",
            entityIndex, serializerName, fieldName, value);
    }

    /// <inheritdoc/>
    public void UnhookEntity(int entityIndex, string serializerName, string fieldName)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName))
            return;

        FieldSubstitution.ClearEntityRegistration(entityIndex, serializerName, fieldName);
        _logger.LogInformation(
            "SendProxy: entity-specific registration removed for ent={Ent} \"{Ser}::{Field}\"",
            entityIndex, serializerName, fieldName);
    }

    /// <inheritdoc/>
    public void UnhookInt(string serializerName, string fieldName)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName))
            return;

        FieldSubstitution.ClearCallback(serializerName, fieldName);
        _logger.LogInformation(
            "SendProxy: global per-client callback removed for \"{Ser}::{Field}\"",
            serializerName, fieldName);
    }

    /// <inheritdoc/>
    public void Unhook(string serializerName, string fieldName)
        => UnhookInt(serializerName, fieldName);

    // ── Float ─────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void HookFloat(string serializerName, string fieldName, PerClientFloatProxy callback)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName) || callback is null)
            return;

        if (!EnsureDetours($"HookFloat(\"{serializerName}::{fieldName}\")"))
            return;

        FieldSubstitution.SetCallback(serializerName, fieldName, callback);
        FieldSubstitution.Mode = SubstitutionMode.Fake;

        _logger.LogInformation(
            "SendProxy: per-client float callback (all entities) registered for \"{Ser}::{Field}\"",
            serializerName, fieldName);
    }

    /// <inheritdoc/>
    public void HookEntityFloat(int entityIndex, string serializerName, string fieldName, PerClientFloatProxy callback)
    {
        if (entityIndex < 0) { _logger.LogWarning("SendProxy: HookEntityFloat — entityIndex must be >= 0 (got {Idx})", entityIndex); return; }
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName) || callback is null)
            return;

        if (!EnsureDetours($"HookEntityFloat(ent={entityIndex}, \"{serializerName}::{fieldName}\")"))
            return;

        FieldSubstitution.SetEntityCallback(entityIndex, serializerName, fieldName, callback);
        FieldSubstitution.Mode = SubstitutionMode.Fake;

        _logger.LogInformation(
            "SendProxy: per-client float callback registered for ent={Ent} \"{Ser}::{Field}\"",
            entityIndex, serializerName, fieldName);
    }

    // ── Bool ──────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void HookBool(string serializerName, string fieldName, PerClientBoolProxy callback)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName) || callback is null)
            return;

        if (!EnsureDetours($"HookBool(\"{serializerName}::{fieldName}\")"))
            return;

        FieldSubstitution.SetCallback(serializerName, fieldName, callback);
        FieldSubstitution.Mode = SubstitutionMode.Fake;

        _logger.LogInformation(
            "SendProxy: per-client bool callback (all entities) registered for \"{Ser}::{Field}\"",
            serializerName, fieldName);
    }

    /// <inheritdoc/>
    public void HookEntityBool(int entityIndex, string serializerName, string fieldName, PerClientBoolProxy callback)
    {
        if (entityIndex < 0) { _logger.LogWarning("SendProxy: HookEntityBool — entityIndex must be >= 0 (got {Idx})", entityIndex); return; }
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName) || callback is null)
            return;

        if (!EnsureDetours($"HookEntityBool(ent={entityIndex}, \"{serializerName}::{fieldName}\")"))
            return;

        FieldSubstitution.SetEntityCallback(entityIndex, serializerName, fieldName, callback);
        FieldSubstitution.Mode = SubstitutionMode.Fake;

        _logger.LogInformation(
            "SendProxy: per-client bool callback registered for ent={Ent} \"{Ser}::{Field}\"",
            entityIndex, serializerName, fieldName);
    }

    // ── Vector / QAngle ───────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void HookVector(string serializerName, string fieldName, PerClientVectorProxy callback)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName) || callback is null)
            return;

        if (!EnsureDetours($"HookVector(\"{serializerName}::{fieldName}\")"))
            return;

        FieldSubstitution.SetCallback(serializerName, fieldName, callback);
        FieldSubstitution.Mode = SubstitutionMode.Fake;

        _logger.LogInformation(
            "SendProxy: per-client vector/qangle callback (all entities) registered for \"{Ser}::{Field}\"",
            serializerName, fieldName);
    }

    /// <inheritdoc/>
    public void HookEntityVector(int entityIndex, string serializerName, string fieldName, PerClientVectorProxy callback)
    {
        if (entityIndex < 0) { _logger.LogWarning("SendProxy: HookEntityVector — entityIndex must be >= 0 (got {Idx})", entityIndex); return; }
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName) || callback is null)
            return;

        if (!EnsureDetours($"HookEntityVector(ent={entityIndex}, \"{serializerName}::{fieldName}\")"))
            return;

        FieldSubstitution.SetEntityCallback(entityIndex, serializerName, fieldName, callback);
        FieldSubstitution.Mode = SubstitutionMode.Fake;

        _logger.LogInformation(
            "SendProxy: per-client vector/qangle callback registered for ent={Ent} \"{Ser}::{Field}\"",
            entityIndex, serializerName, fieldName);
    }

    /// <inheritdoc/>
    public void UnhookAllPerClient()
    {
        // Clear the entire registry (global + entity-specific) and uninstall detours.
        FieldSubstitution.ClearAll();
        FieldSubstitution.Uninstall();
        _logger.LogInformation("SendProxy: all per-client registrations cleared, Phase-2 detours uninstalled");
    }

    internal void Clear()
    {
        _hooks.Clear();
        _changeHooks.Clear();
    }
}
