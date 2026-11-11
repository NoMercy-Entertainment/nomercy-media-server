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
using NoMercy.Events;
using NoMercy.Events.Plugins;
using NoMercy.Networking.Messaging;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Access;
using NoMercy.PluginSdk.Capabilities;

namespace NoMercy.Api.Hubs;

/// <summary>
/// Sends one account its own answer, on every device it is signed in on.
/// <para>
/// Addressed to the user rather than to a connection, which is what makes the
/// phone and the television agree without either of them asking again.
/// </para>
/// Also removes plugin group memberships when access or plugin availability ends.
/// </summary>
public class PluginAccessHubSender : IPluginAccessHub, IDisposable
{
    private readonly IHubContext<PluginHub> _hub;
    private readonly ConnectedClients? _connectedClients;
    private readonly IPluginAccessResolver? _access;
    private readonly IPluginManifestSource? _plugins;
    private readonly IDisposable? _accessSubscription;
    private readonly IDisposable? _disabledSubscription;

    public PluginAccessHubSender(IHubContext<PluginHub> hub) => _hub = hub;

    public PluginAccessHubSender(
        IHubContext<PluginHub> hub,
        ConnectedClients connectedClients,
        IPluginAccessResolver access,
        IPluginManifestSource plugins,
        IEventBus events
    )
        : this(hub)
    {
        _connectedClients = connectedClients;
        _access = access;
        _plugins = plugins;
        _accessSubscription = events.Subscribe<PluginAccessChangedEvent>(OnAccessChanged);
        _disabledSubscription = events.Subscribe<PluginDisabledEvent>(OnDisabled);
    }

    public void Send(Guid userId, Ulid pluginId, string access) =>
        _hub
            .Clients.User(userId.ToString())
            .SendAsync("PluginAccessChanged", new { pluginId, access });

    private Task OnAccessChanged(PluginAccessChangedEvent changed, CancellationToken ct)
    {
        if (Ulid.TryParse(changed.PluginId, out Ulid pluginId) && pluginId != Ulid.Empty)
            return RevokeLostAccessAsync(pluginId, ct);

        return Task.WhenAll(_plugins!.All().Select(plugin => RevokeLostAccessAsync(plugin.Id, ct)));
    }

    private Task RevokeLostAccessAsync(Ulid pluginId, CancellationToken ct)
    {
        // A removed member is no longer in the notifier's EveryoneOn list, but
        // their still-open hub connection must leave the plugin group.
        string group = PluginHub.GroupFor(pluginId);
        return Task.WhenAll(
            _connectedClients!
                .Clients.Where(connection =>
                    connection.Value.Endpoint == "/pluginHub"
                    && _access!.Resolve(pluginId, connection.Value.Sub) == PluginAccess.None
                )
                .Select(connection => _hub.Groups.RemoveFromGroupAsync(connection.Key, group, ct))
        );
    }

    private Task OnDisabled(PluginDisabledEvent disabled, CancellationToken ct)
    {
        if (!Ulid.TryParse(disabled.PluginId, out Ulid pluginId))
            return Task.CompletedTask;

        string group = PluginHub.GroupFor(pluginId);
        return Task.WhenAll(
            _connectedClients!
                .Clients.Where(connection => connection.Value.Endpoint == "/pluginHub")
                .Select(connection => _hub.Groups.RemoveFromGroupAsync(connection.Key, group, ct))
        );
    }

    public void Dispose()
    {
        _accessSubscription?.Dispose();
        _disabledSubscription?.Dispose();
    }
}
