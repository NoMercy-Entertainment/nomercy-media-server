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

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Hub;

namespace NoMercy.Api.Hubs;

/// <summary>
/// A plugin's push channel. Bound to one plugin id at construction, so a
/// plugin cannot reach another plugin's subscribers even by trying.
/// </summary>
public class PluginHubBroadcaster(
    IHubContext<PluginHub> hubContext,
    Ulid pluginId,
    IPluginHubRouter router
) : IPluginHubContext
{
    /// <summary>
    /// Registers one delegate per method name with the router, through the same
    /// path a plugin implementing IPluginHubHandler takes. Built on first use
    /// so a plugin that never registers anything costs nothing.
    /// </summary>
    public void Handle(
        string method,
        Func<PluginCaller, JsonNode?, CancellationToken, Task<object?>> handler
    )
    {
        PluginDelegateHubHandler delegateHandler = router.DelegateHandlerFor(pluginId);
        delegateHandler.Register(method, handler);
    }

    public Task PushAsync(string type, object? payload) =>
        hubContext
            .Clients.Group(PluginHub.GroupFor(pluginId))
            .SendAsync(
                "PluginMessage",
                new
                {
                    pluginId,
                    type,
                    payload,
                }
            );

    public Task PushToUserAsync(string userId, string type, object? payload) =>
        hubContext
            .Clients.User(userId)
            .SendAsync(
                "PluginMessage",
                new
                {
                    pluginId,
                    type,
                    payload,
                }
            );
}
