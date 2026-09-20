// -----------------------------------------------------------------------------
//  Copyright (c) 2024-present NoMercy Entertainment. All rights reserved.
//
//  This file is part of NoMercy MediaServer, source-available software (NOT open
//  source). Personal use and contributions are welcome; distribution, resale,
//  relicensing, and commercial exploitation are prohibited without explicit
//  written consent. See LICENSE for full terms. Distributed WITHOUT ANY WARRANTY.
//
//  SPDX-License-Identifier: LicenseRef-NoMercy-Proprietary
// -----------------------------------------------------------------------------

using Microsoft.AspNetCore.SignalR;
using NoMercy.Plugins.Access;

namespace NoMercy.Api.Hubs;

/// <summary>
/// Sends one account its own answer, on every device it is signed in on.
/// <para>
/// Addressed to the user rather than to a connection, which is what makes the
/// phone and the television agree without either of them asking again.
/// </para>
/// </summary>
public class PluginAccessHubSender(IHubContext<PluginHub> hub) : IPluginAccessHub
{
    public void Send(Guid userId, Ulid pluginId, string access) =>
        hub
            .Clients.User(userId.ToString())
            .SendAsync("PluginAccessChanged", new { pluginId, access });
}
