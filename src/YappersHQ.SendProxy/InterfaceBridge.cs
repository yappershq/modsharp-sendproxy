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

using Sharp.Shared;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;

namespace YappersHQ.SendProxy;

internal sealed class InterfaceBridge
{
    public IGameData            GameData           { get; }
    public IEntityManager       EntityManager      { get; }
    public IHookManager         HookManager        { get; }
    public ISharpModuleManager  SharpModuleManager { get; }

    public InterfaceBridge(ISharedSystem sharedSystem)
    {
        GameData           = sharedSystem.GetModSharp().GetGameData();
        EntityManager      = sharedSystem.GetEntityManager();
        HookManager        = sharedSystem.GetHookManager();
        SharpModuleManager = sharedSystem.GetSharpModuleManager();
    }
}
