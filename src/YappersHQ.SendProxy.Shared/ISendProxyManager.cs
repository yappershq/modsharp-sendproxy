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
// PerClientIntProxy fires on engine send threads (~6 workers) — MUST be thread-safe and non-blocking.
//   client      — CServerSideClient* (0 if RecipientCapture not active)
//   entityIndex — from WDE ctx+0x34, -1 if not captured
//   value       — ref int, pre-seeded with the registered uniform spoof value (or 0)
//   returns     — true → substitute `value`; false → passthrough original
//
// The substitution is encoded with the FIELD's real network type (auto-detected): signed int
// (int8/16/32/64) as zigzag varint, unsigned int as raw varint, bool as 1 bit (value != 0), and
// float32 as 32 raw bits. For a float field, pass the bits via BitConverter.SingleToInt32Bits(f);
// for a bool, pass 0/1. If the field's type can't be classified, the hook passes through untouched
// (it never writes wrong-type bits). The single int carrier keeps the low-level API minimal.

public delegate bool PerClientIntProxy(nint client, int entityIndex, ref int value);

/// <summary>
///     Per-client float field substitution callback.
///     <paramref name="value"/> is seeded 0f — write the desired float and return true to substitute,
///     false to pass through the real value.
///     Runs on engine send threads — must be thread-safe and non-blocking.
/// </summary>
public delegate bool PerClientFloatProxy(nint client, int entityIndex, ref float value);

/// <summary>
///     Per-client bool field substitution callback.
///     <paramref name="value"/> is seeded false — write the desired bool and return true to substitute,
///     false to pass through the real value.
///     Runs on engine send threads — must be thread-safe and non-blocking.
/// </summary>
public delegate bool PerClientBoolProxy(nint client, int entityIndex, ref bool value);

/// <summary>
///     Per-client vector/qangle field substitution callback.
///     x/y/z are seeded 0f — write the desired components and return true to substitute,
///     false to pass through the real value.
///     Applies to QAngle3 and Vector3 fields (3 contiguous float32 in the engine's native layout).
///     Runs on engine send threads — must be thread-safe and non-blocking.
/// </summary>
public delegate bool PerClientVectorProxy(nint client, int entityIndex, ref float x, ref float y, ref float z);

// ── Proxy callbacks (per-type, value passed by ref) ──────────────────────────────

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
    // Registers a callback on the raw (serializer, field) pair via FieldSubstitution — bypasses
    // entity/hook bookkeeping. Installs Phase-2 sub-detours (WFL shim, GetBitRange, value-copy,
    // WDE entity-index capture) on first use and switches to Fake mode.
    // Thread safety: callback runs on ~6 engine send threads — must be thread-safe and fast.
    // Exception safety: a throwing callback is caught → passthrough for that field/client.

    /// <summary>
    ///     Register a per-client int substitution callback for (serializerName, fieldName).
    ///     Fires for ALL entities of that serializer (entityIndex == -1 scope).
    ///     Replaces any existing global callback for the same key. Installs Phase-2 detours on first use.
    /// </summary>
    void HookInt(string serializerName, string fieldName, PerClientIntProxy callback);

    /// <summary>
    ///     Register a per-client int substitution callback scoped to a SPECIFIC entity index.
    ///     When both a per-entity and a global (-1) registration exist for the same (ser, field),
    ///     the per-entity registration wins for that entity's delta sends.
    ///     Installs Phase-2 detours on first use.
    /// </summary>
    void HookEntityInt(int entityIndex, string serializerName, string fieldName, PerClientIntProxy callback);

    /// <summary>
    ///     Register a uniform (same value for every client) int spoof scoped to a SPECIFIC entity index.
    ///     Convenience alternative to <see cref="HookEntityInt"/> when no per-client logic is needed.
    ///     Installs Phase-2 detours on first use.
    /// </summary>
    void SetEntitySpoof(int entityIndex, string serializerName, string fieldName, int value);

    /// <summary>
    ///     Remove the entity-specific registration for (entityIndex, serializerName, fieldName).
    ///     Does NOT affect the global (-1) registration for the same field.
    ///     Does NOT uninstall Phase-2 detours.
    /// </summary>
    void UnhookEntity(int entityIndex, string serializerName, string fieldName);

    /// <summary>
    ///     Remove the global per-client callback for (serializerName, fieldName).
    ///     Does NOT uninstall Phase-2 detours (call <c>UnhookAllPerClient</c> for that).
    /// </summary>
    void UnhookInt(string serializerName, string fieldName);

    // ── Typed per-client API: Float ───────────────────────────────────────────────────────────

    /// <summary>
    ///     Register a per-client float substitution callback for (serializerName, fieldName), all entities.
    ///     Use for fields classified Float32. Installs Phase-2 detours on first use.
    /// </summary>
    void HookFloat(string serializerName, string fieldName, PerClientFloatProxy callback);

    /// <summary>
    ///     Register a per-client float substitution callback scoped to a SPECIFIC entity index.
    ///     Installs Phase-2 detours on first use.
    /// </summary>
    void HookEntityFloat(int entityIndex, string serializerName, string fieldName, PerClientFloatProxy callback);

    // ── Typed per-client API: Bool ────────────────────────────────────────────────────────────

    /// <summary>
    ///     Register a per-client bool substitution callback for (serializerName, fieldName), all entities.
    ///     Use for fields classified Bool. Installs Phase-2 detours on first use.
    /// </summary>
    void HookBool(string serializerName, string fieldName, PerClientBoolProxy callback);

    /// <summary>
    ///     Register a per-client bool substitution callback scoped to a SPECIFIC entity index.
    ///     Installs Phase-2 detours on first use.
    /// </summary>
    void HookEntityBool(int entityIndex, string serializerName, string fieldName, PerClientBoolProxy callback);

    // ── Typed per-client API: Vector/QAngle ──────────────────────────────────────────────────

    /// <summary>
    ///     Register a per-client vector/qangle substitution callback for (serializerName, fieldName), all entities.
    ///     Use for fields classified QAngle3 or Vector3 (3 contiguous float32 in the engine layout).
    ///     Installs Phase-2 detours on first use.
    /// </summary>
    void HookVector(string serializerName, string fieldName, PerClientVectorProxy callback);

    /// <summary>
    ///     Register a per-client vector/qangle substitution callback scoped to a SPECIFIC entity index.
    ///     Installs Phase-2 detours on first use.
    /// </summary>
    void HookEntityVector(int entityIndex, string serializerName, string fieldName, PerClientVectorProxy callback);

    /// <summary>
    ///     Remove the global per-client callback for (serializerName, fieldName) regardless of type.
    ///     Alias for UnhookInt — type-agnostic removal.
    ///     Does NOT uninstall Phase-2 detours (call <c>UnhookAllPerClient</c> for that).
    /// </summary>
    void Unhook(string serializerName, string fieldName);

    /// <summary>
    ///     Remove ALL registered per-client callbacks and entity-specific spoofs/callbacks,
    ///     then uninstall the Phase-2 sub-detours.
    ///     Use this during plugin shutdown if you registered any callbacks.
    /// </summary>
    void UnhookAllPerClient();

    // ── Uniform (all-entity) spoof helpers ───────────────────────────────────────────────────

    /// <summary>
    ///     Set a uniform int spoof on (serializerName, fieldName) for ALL entities of that serializer.
    ///     Every client will see <paramref name="value"/> for this field regardless of the real server value.
    ///     Installs Phase-2 detours on first use.
    /// </summary>
    void SetUniformInt(string serializerName, string fieldName, int value);

    /// <summary>
    ///     Set a uniform int spoof scoped to a SPECIFIC entity index.
    ///     Every client will see <paramref name="value"/> for that entity's field; other entities are unaffected.
    ///     Installs Phase-2 detours on first use.
    /// </summary>
    void SetUniformIntForEntity(int entityIndex, string serializerName, string fieldName, int value);
}
