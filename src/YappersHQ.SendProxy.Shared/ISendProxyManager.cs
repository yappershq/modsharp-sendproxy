/*
 * SendProxy for ModSharp (CS2)
 * Copyright (C) 2026 YappersHQ. All Rights Reserved.
 *
 * This file is part of SendProxy for ModSharp.
 * SendProxy is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as
 * published by the Free Software Foundation, either version 3 of the
 * License, or (at your option) any later version.
 *
 * SendProxy is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with SendProxy. If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Numerics;
using Sharp.Shared.Objects;

namespace YappersHQ.SendProxy.Shared;

/// <summary>
///     Networked-field type a hook targets. Mirrors SourceMod SendProxy's <c>SendPropType</c>.
/// </summary>
public enum SendPropType
{
    Int    = 0,
    Float  = 1,
    String = 2,
    Vector = 4,
}

/// <summary>
///     Result of a proxy callback. <see cref="Changed"/> = the engine encodes the value the callback
///     wrote back (by ref); <see cref="Unchanged"/> = the real value is encoded. Mirrors SourceMod's
///     <c>Plugin_Changed</c> / <c>Plugin_Continue</c>.
/// </summary>
public enum SendProxyResult
{
    Unchanged = 0,
    Changed   = 1,
}

/// <summary>
///     Per-client int field substitution callback. Runs on the engine send worker threads — must be
///     thread-safe and non-blocking. <paramref name="value"/> is seeded with the registered uniform
///     spoof value (or 0); return true to substitute it, false to pass through the real value.
///     <paramref name="client"/> is the raw <c>CServerSideClient*</c> (0 if recipient capture is
///     inactive); <paramref name="entityIndex"/> is the entity being sent (-1 if not captured).
/// </summary>
public delegate bool PerClientIntProxy(nint client, int entityIndex, ref int value);

/// <summary>
///     Per-client float field substitution callback. <paramref name="value"/> is seeded 0f — write the
///     desired float and return true to substitute, false to pass through. Runs on send threads.
/// </summary>
public delegate bool PerClientFloatProxy(nint client, int entityIndex, ref float value);

/// <summary>
///     Per-client bool field substitution callback. <paramref name="value"/> is seeded false — write the
///     desired bool and return true to substitute, false to pass through. Runs on send threads.
/// </summary>
public delegate bool PerClientBoolProxy(nint client, int entityIndex, ref bool value);

/// <summary>
///     Per-client vector/qangle field substitution callback. x/y/z are seeded 0f — write the desired
///     components and return true to substitute, false to pass through. Applies to QAngle3 and Vector3
///     fields (3 contiguous float32 in the engine layout). Runs on send threads.
/// </summary>
public delegate bool PerClientVectorProxy(nint client, int entityIndex, ref float x, ref float y, ref float z);

/// <summary>Entity-scoped int proxy callback (value passed by ref).</summary>
public delegate SendProxyResult SendProxyIntCallback(IGameClient? client, int entity, string prop, int element, ref int value);

/// <summary>Entity-scoped float proxy callback (value passed by ref).</summary>
public delegate SendProxyResult SendProxyFloatCallback(IGameClient? client, int entity, string prop, int element, ref float value);

/// <summary>Entity-scoped string proxy callback (value passed by ref).</summary>
public delegate SendProxyResult SendProxyStringCallback(IGameClient? client, int entity, string prop, int element, ref string value);

/// <summary>Entity-scoped vector proxy callback (value passed by ref).</summary>
public delegate SendProxyResult SendProxyVectorCallback(IGameClient? client, int entity, string prop, int element, ref Vector3 value);

/// <summary>Fired (polling diff) when a watched field's real value changes. Mirrors SM's PropChange hook.</summary>
public delegate void PropChangeCallback(int entity, string prop, string oldValue, string newValue);

/// <summary>
///     SendProxy for ModSharp / CS2 — intercept the per-client serialization of networked entity fields
///     and substitute values, without changing the real server-side state. The public surface mirrors
///     SourceMod's SendProxyManager (<c>sendproxy.inc</c>); the per-client overloads add the recipient
///     (matrix) dimension.
/// </summary>
public interface ISendProxyManager
{
    const string Identity = nameof(ISendProxyManager);

    bool HookInt(int entity, string prop, SendProxyIntCallback callback);

    bool HookFloat(int entity, string prop, SendProxyFloatCallback callback);

    bool HookString(int entity, string prop, SendProxyStringCallback callback);

    bool HookVector(int entity, string prop, SendProxyVectorCallback callback);

    bool HookArrayInt(int entity, string prop, int element, SendProxyIntCallback callback);

    bool HookArrayFloat(int entity, string prop, int element, SendProxyFloatCallback callback);

    bool HookGameRulesInt(string prop, SendProxyIntCallback callback);

    bool HookGameRulesFloat(string prop, SendProxyFloatCallback callback);

    bool Unhook(int entity, string prop, Delegate callback);

    /// <summary>
    ///     Remove ALL hooks on (entity, prop). Use this when you registered with a lambda (which can't be
    ///     matched by reference in the delegate overload).
    /// </summary>
    bool Unhook(int entity, string prop);

    bool UnhookGameRules(string prop, Delegate callback);

    bool IsHooked(int entity, string prop);

    bool IsHookedGameRules(string prop);

    bool HookPropChange(int entity, string prop, PropChangeCallback callback);

    bool UnhookPropChange(int entity, string prop, PropChangeCallback callback);

    /// <summary>
    ///     Register a per-client int substitution callback for (serializerName, fieldName). Fires for ALL
    ///     entities of that serializer. Replaces any existing global callback for the same key. Installs
    ///     the substitution detours on first use. The callback runs on engine send threads — keep it
    ///     thread-safe and fast; a throwing callback is caught and treated as passthrough.
    /// </summary>
    void HookInt(string serializerName, string fieldName, PerClientIntProxy callback);

    /// <summary>
    ///     Register a per-client int substitution callback scoped to a SPECIFIC entity index. When both a
    ///     per-entity and a global registration exist for the same (ser, field), the per-entity one wins
    ///     for that entity. Installs the substitution detours on first use.
    /// </summary>
    void HookEntityInt(int entityIndex, string serializerName, string fieldName, PerClientIntProxy callback);

    /// <summary>
    ///     Register a uniform (same value for every client) int spoof scoped to a SPECIFIC entity index.
    ///     Installs the substitution detours on first use.
    /// </summary>
    void SetEntitySpoof(int entityIndex, string serializerName, string fieldName, int value);

    /// <summary>
    ///     Remove the entity-specific registration for (entityIndex, serializerName, fieldName). Does not
    ///     affect the global registration for the same field, and does not uninstall the detours.
    /// </summary>
    void UnhookEntity(int entityIndex, string serializerName, string fieldName);

    /// <summary>
    ///     Remove the global per-client callback for (serializerName, fieldName). Does not uninstall the
    ///     detours (call <see cref="UnhookAllPerClient"/> for that).
    /// </summary>
    void UnhookInt(string serializerName, string fieldName);

    /// <summary>
    ///     Register a per-client float substitution callback for (serializerName, fieldName), all entities.
    ///     Use for fields classified Float32. Installs the substitution detours on first use.
    /// </summary>
    void HookFloat(string serializerName, string fieldName, PerClientFloatProxy callback);

    /// <summary>
    ///     Register a per-client float substitution callback scoped to a SPECIFIC entity index. Installs
    ///     the substitution detours on first use.
    /// </summary>
    void HookEntityFloat(int entityIndex, string serializerName, string fieldName, PerClientFloatProxy callback);

    /// <summary>
    ///     Register a per-client bool substitution callback for (serializerName, fieldName), all entities.
    ///     Use for fields classified Bool. Installs the substitution detours on first use.
    /// </summary>
    void HookBool(string serializerName, string fieldName, PerClientBoolProxy callback);

    /// <summary>
    ///     Register a per-client bool substitution callback scoped to a SPECIFIC entity index. Installs the
    ///     substitution detours on first use.
    /// </summary>
    void HookEntityBool(int entityIndex, string serializerName, string fieldName, PerClientBoolProxy callback);

    /// <summary>
    ///     Register a per-client vector/qangle substitution callback for (serializerName, fieldName), all
    ///     entities. Use for fields classified QAngle3 or Vector3 (3 contiguous float32 in the engine
    ///     layout). Installs the substitution detours on first use.
    /// </summary>
    void HookVector(string serializerName, string fieldName, PerClientVectorProxy callback);

    /// <summary>
    ///     Register a per-client vector/qangle substitution callback scoped to a SPECIFIC entity index.
    ///     Installs the substitution detours on first use.
    /// </summary>
    void HookEntityVector(int entityIndex, string serializerName, string fieldName, PerClientVectorProxy callback);

    /// <summary>
    ///     Remove the global per-client callback for (serializerName, fieldName) regardless of type.
    ///     Type-agnostic alias for <see cref="UnhookInt"/>. Does not uninstall the detours.
    /// </summary>
    void Unhook(string serializerName, string fieldName);

    /// <summary>
    ///     Remove ALL registered per-client callbacks and entity-specific spoofs/callbacks, then uninstall
    ///     the substitution detours. Use this during plugin shutdown if you registered any callbacks.
    /// </summary>
    void UnhookAllPerClient();

    /// <summary>
    ///     Set a uniform int spoof on (serializerName, fieldName) for ALL entities of that serializer.
    ///     Every client sees <paramref name="value"/> regardless of the real server value. Installs the
    ///     substitution detours on first use.
    /// </summary>
    void SetUniformInt(string serializerName, string fieldName, int value);

    /// <summary>
    ///     Set a uniform int spoof scoped to a SPECIFIC entity index. Installs the substitution detours on
    ///     first use.
    /// </summary>
    void SetUniformIntForEntity(int entityIndex, string serializerName, string fieldName, int value);
}
