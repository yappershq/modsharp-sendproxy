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
using System.Globalization;
using System.Numerics;
using Microsoft.Extensions.Logging;
using Sharp.Modules.TargetingManager.Shared;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using YappersHQ.SendProxy.Shared;

namespace YappersHQ.SendProxy.Example;

internal sealed class ExampleContext
{
    public const string Permission = "sendproxy:example";

    // Cache the interface WRAPPER, not the instance: GetOptionalSharpModuleInterface returns a handle that
    // tracks the live interface, so reading .Instance per use always gives the current SendProxy even if it
    // hot-reloads. Caching the raw .Instance would dangle on reload (per ModSharp authors / laper).
    public IModSharpModuleInterface<ISendProxyManager>? Handle { get; set; }
    public ISendProxyManager?                           SendProxy => Handle?.Instance;

    public IEntityManager                              EntityManager { get; }
    public IClientManager                              ClientManager { get; }
    public IModSharp                                   ModSharp      { get; }
    public ISchemaManager                              Schema        { get; }
    public IModSharpModuleInterface<ITargetingManager>? Targeting    { get; set; }
    public ILogger                                     Logger        { get; }

    public ExampleContext(
        IEntityManager entityManager,
        IClientManager clientManager,
        IModSharp      modSharp,
        ISchemaManager schema,
        ILogger        logger)
    {
        EntityManager = entityManager;
        ClientManager = clientManager;
        ModSharp      = modSharp;
        Schema        = schema;
        Logger        = logger;
    }

    public void Reply(IGameClient? issuer, string message)
    {
        // Print at the CLIENT level, not via the pawn — pawn.Print routes through the pawn's controller
        // and throws "Controller is null" when the command comes from console/RCON or the issuer's pawn
        // isn't linked to a controller. issuer.Print goes straight to the client. Fall back to the server
        // log for console invocations (issuer == null) or if the client print throws for any reason.
        if (issuer is { IsValid: true })
        {
            try
            {
                issuer.Print(HudPrintChannel.Chat, message);

                return;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "SendProxy example: client Print failed — logging instead");
            }
        }

        Logger.LogInformation("{Message}", message);
    }

    public void ForceResendAll(string serializerName, string fieldName)
    {
        var onController = serializerName.Contains("Controller", StringComparison.Ordinal);
        foreach (var client in ClientManager.GetGameClients(inGame: true))
        {
            // Bots have no screen and the engine never sends them snapshots, so there's nothing to
            // re-transmit on their behalf — skip them.
            if (client.IsFakeClient || client.IsHltv)
            {
                continue;
            }

            if (client.GetPlayerController() is not { } controller)
            {
                continue;
            }

            if (onController)
            {
                controller.NetworkStateChanged(fieldName);
            }
            else if (controller.GetPlayerPawn() is { } pawn)
            {
                pawn.NetworkStateChanged(fieldName);
            }
        }
    }

    // ── Static parse helpers ──────────────────────────────────────────────────────────────────────

    public static bool TryInt(StringCommand cmd, int argIndex, out int value)
    {
        value = 0;
        if (cmd.ArgCount < argIndex
            || !long.TryParse(cmd.GetArg(argIndex), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
        {
            return false;
        }

        value = unchecked((int) l);

        return true;
    }

    public static bool TryFloat(StringCommand cmd, int argIndex, out float value)
    {
        value = 0f;

        return cmd.ArgCount >= argIndex
            && float.TryParse(cmd.GetArg(argIndex), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public static bool TryBool(StringCommand cmd, int argIndex, out bool value)
    {
        value = false;
        if (cmd.ArgCount < argIndex)
        {
            return false;
        }

        var s = cmd.GetArg(argIndex);
        if (s is "1" or "true" or "yes")
        {
            value = true;

            return true;
        }

        return s is "0" or "false" or "no";
    }

    public static bool TryVec(StringCommand cmd, int argIndex, out Vector3 value)
    {
        value = default;
        if (cmd.ArgCount < argIndex + 2
            || !float.TryParse(cmd.GetArg(argIndex), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(cmd.GetArg(argIndex + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !float.TryParse(cmd.GetArg(argIndex + 2), NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
        {
            return false;
        }

        value = new Vector3(x, y, z);

        return true;
    }

    // Parse a contiguous hex string (e.g. "DEADBEEF") into bytes.
    public static bool TryBytes(StringCommand cmd, int argIndex, out byte[] value)
    {
        value = Array.Empty<byte>();
        if (cmd.ArgCount < argIndex)
        {
            return false;
        }

        var hex = cmd.GetArg(argIndex);
        if (hex.Length == 0 || (hex.Length & 1) != 0)
        {
            return false;
        }

        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
            {
                return false;
            }
        }

        value = bytes;

        return true;
    }

    // Join arg[from..ArgCount] with spaces (for free-text string values).
    public static string RestOfArgs(StringCommand cmd, int from)
    {
        if (cmd.ArgCount < from)
        {
            return string.Empty;
        }

        var parts = new string[cmd.ArgCount - from + 1];
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = cmd.GetArg(from + i);
        }

        return string.Join(' ', parts);
    }
}
