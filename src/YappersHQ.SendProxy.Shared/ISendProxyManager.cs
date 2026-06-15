using System.Numerics;
using Sharp.Shared.Objects;

namespace YappersHQ.SendProxy.Shared;

/// <summary>
///     Networked-field type a hook targets. Mirrors SourceMod SendProxy's <c>SendPropType</c>.
/// </summary>
public enum SendPropType
{
    Int = 0,
    Float = 1,
    String = 2,
    Vector = 4,
}

/// <summary>
///     Result of a proxy callback. <see cref="Changed"/> = the engine encodes the value the
///     callback wrote back (by ref); <see cref="Unchanged"/> = the real value is encoded.
///     Mirrors SourceMod's <c>Plugin_Changed</c> / <c>Plugin_Continue</c>.
/// </summary>
public enum SendProxyResult
{
    Unchanged = 0,
    Changed = 1,
}

// ── Per-client raw callback (Phase-2 low-level API) ──────────────────────────────
//
// PerClientIntProxy is the low-level per-field callback used by FieldSubstitution.
// It fires on engine send threads (~6 workers) — MUST be thread-safe and non-blocking.
//
// Parameters:
//   client      — raw CServerSideClient* (0 if RecipientCapture is not active)
//   entityIndex — entity being encoded (from WDE ctx+0x34), -1 if not captured
//   value       — ref int, pre-seeded with the registered uniform spoof value (or 0);
//                 the callback may overwrite this with any desired value
//
// Returns:
//   true  — substitute `value` into the encoded stream for this client
//   false — leave the original value unchanged (passthrough)
//
// Exception safety: a throwing callback is caught by FieldSubstitution → passthrough.

public delegate bool PerClientIntProxy(nint client, int entityIndex, ref int value);

// ── Proxy callbacks (per-type, value passed by ref) ──────────────────────────────
// `client` is the recipient the field is being encoded for. It is null for a uniform
// (all-clients) encode pass; non-null when per-client encoding is active (Phase 2).
// `entity` is the entity index, `element` the array element (0 for scalar fields).

public delegate SendProxyResult SendProxyIntCallback(IGameClient? client, int entity, string prop, int element, ref int value);

public delegate SendProxyResult SendProxyFloatCallback(IGameClient? client, int entity, string prop, int element, ref float value);

public delegate SendProxyResult SendProxyStringCallback(IGameClient? client, int entity, string prop, int element, ref string value);

public delegate SendProxyResult SendProxyVectorCallback(IGameClient? client, int entity, string prop, int element, ref Vector3 value);

/// <summary>Fired (polling diff) when a watched field's real value changes. Mirrors SM's PropChange hook.</summary>
public delegate void PropChangeCallback(int entity, string prop, string oldValue, string newValue);

/// <summary>
///     SendProxy for ModSharp / CS2 — intercept the per-client serialization of networked
///     entity fields and substitute values, without changing the real server-side state.
///     Public surface mirrors SourceMod's SendProxyManager (<c>sendproxy.inc</c>); the
///     <see cref="IGameClient"/> recipient argument adds the per-client (matrix) dimension.
/// </summary>
public interface ISendProxyManager
{
    const string Identity = nameof(ISendProxyManager);

    bool HookInt(int entity, string prop, SendProxyIntCallback callback);
    bool HookFloat(int entity, string prop, SendProxyFloatCallback callback);
    bool HookString(int entity, string prop, SendProxyStringCallback callback);
    bool HookVector(int entity, string prop, SendProxyVectorCallback callback);

    // Array element variants (hook a single element of an array/utlvector field).
    bool HookArrayInt(int entity, string prop, int element, SendProxyIntCallback callback);
    bool HookArrayFloat(int entity, string prop, int element, SendProxyFloatCallback callback);

    // CCSGameRules proxy (no entity index — the gamerules proxy entity).
    bool HookGameRulesInt(string prop, SendProxyIntCallback callback);
    bool HookGameRulesFloat(string prop, SendProxyFloatCallback callback);

    bool Unhook(int entity, string prop, System.Delegate callback);

    /// <summary>
    ///     Remove ALL hooks on (entity, prop). Use this when you registered with a lambda (which
    ///     can't be matched by reference in the delegate overload).
    /// </summary>
    bool Unhook(int entity, string prop);
    bool UnhookGameRules(string prop, System.Delegate callback);
    bool IsHooked(int entity, string prop);
    bool IsHookedGameRules(string prop);

    bool HookPropChange(int entity, string prop, PropChangeCallback callback);
    bool UnhookPropChange(int entity, string prop, PropChangeCallback callback);

    // ── Phase-2 per-client raw callback API ──────────────────────────────────────────────────
    //
    // HookInt(serializerName, fieldName, callback) registers a per-client callback on the named
    // (serializer, field) pair at the FieldSubstitution level — bypassing the entity/hook
    // bookkeeping used by HookInt(entity, prop, ...).  This is the Phase-2 substitution path
    // that fires per client, per frame, for every entity that has the named field changed.
    //
    // This installs the Phase-2 sub-detours (WFL shim, GetBitRange, value-copy, WDE capture)
    // and sets mode = Fake.  The uniform spoof (if any) for the same key is used as the
    // initial `value` seed passed to the callback.
    //
    // Thread safety: callback runs on ~6 engine send threads — must be thread-safe + fast.
    // Exception safety: a throwing callback is caught → passthrough for that field/client.

    /// <summary>
    ///     Register a per-client int substitution callback for a raw (serializerName, fieldName)
    ///     pair.  Replaces any existing callback for the same key.  Installs Phase-2 detours
    ///     and switches to Fake mode if not already active.
    /// </summary>
    void HookInt(string serializerName, string fieldName, PerClientIntProxy callback);

    /// <summary>
    ///     Remove the per-client callback for (serializerName, fieldName).
    ///     Does NOT uninstall Phase-2 detours (call <c>UnhookAllPerClient</c> for that).
    /// </summary>
    void UnhookInt(string serializerName, string fieldName);

    /// <summary>
    ///     Remove ALL registered per-client callbacks and uninstall the Phase-2 sub-detours.
    ///     Use this during plugin shutdown if you registered any callbacks.
    /// </summary>
    void UnhookAllPerClient();
}
