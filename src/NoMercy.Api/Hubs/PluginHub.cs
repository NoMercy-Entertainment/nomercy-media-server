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
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Database.Activity;
using NoMercy.Networking;
using NoMercy.Networking.Messaging;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Hub;

namespace NoMercy.Api.Hubs;

/// <summary>
/// One hub for every plugin, multiplexed by group.
/// <para>
/// A hub per plugin would mean mapping endpoints at startup for plugins that
/// are installed later, and a client opening a connection per plugin. Instead a
/// client subscribes to <c>plugin:{id}</c> on this one, and the router decides
/// whether the plugin behind that id is allowed to receive anything.
/// </para>
/// </summary>
public class PluginHub(
    IHttpContextAccessor httpContextAccessor,
    IDbContextFactory<MediaContext> contextFactory,
    ConnectedClients connectedClients,
    IActivityLogger activityLogger,
    IPluginHubRouter router
) : ConnectionHub(httpContextAccessor, contextFactory, connectedClients, activityLogger)
{
    public static string GroupFor(Ulid pluginId) => $"plugin:{pluginId}";

    public Task Subscribe(string pluginId) =>
        Ulid.TryParse(pluginId, out Ulid id)
            ? Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(id))
            : Task.CompletedTask;

    public Task Unsubscribe(string pluginId) =>
        Ulid.TryParse(pluginId, out Ulid id)
            ? Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(id))
            : Task.CompletedTask;

    public async Task<bool> Send(string pluginId, string method, JsonNode? payload)
    {
        if (!Ulid.TryParse(pluginId, out Ulid id))
            return false;

        PluginHubMessage message = new()
        {
            Method = method,
            Payload = payload,
            ConnectionId = Context.ConnectionId,
            UserId = Context.UserIdentifier,
        };

        return await router.RouteAsync(
            id,
            message,
            new PluginHubCallerClient(Clients.Caller, id),
            Context.ConnectionAborted
        );
    }
}
