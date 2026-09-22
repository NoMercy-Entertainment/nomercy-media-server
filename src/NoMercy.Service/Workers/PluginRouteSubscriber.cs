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

using NoMercy.Api.Plugins;
using NoMercy.Events;
using NoMercy.Events.Plugins;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Service.Workers;

/// <summary>
/// Keeps MVC's route table matching which plugins are actually running.
/// <para>
/// A plugin enabled from the dashboard should serve its endpoints without the
/// owner restarting the server, and a plugin disabled there should stop serving
/// them the same way. Both are driven off the lifecycle events rather than a
/// call from the plugin manager, because the manager must not depend on MVC.
/// </para>
/// </summary>
public class PluginRouteSubscriber(
    IEventBus eventBus,
    PluginApplicationPartRegistrar registrar,
    IPluginManager pluginManager,
    ILogger<PluginRouteSubscriber> logger
) : IHostedService
{
    private readonly List<IDisposable> _subscriptions = [];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscriptions.Add(eventBus.Subscribe<PluginLoadedEvent>(OnLoaded));
        _subscriptions.Add(eventBus.Subscribe<PluginDisabledEvent>(OnDisabled));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (IDisposable subscription in _subscriptions)
            subscription.Dispose();

        _subscriptions.Clear();
        return Task.CompletedTask;
    }

    private Task OnLoaded(PluginLoadedEvent loaded, CancellationToken ct)
    {
        if (!Ulid.TryParse(loaded.PluginId, out Ulid pluginId))
            return Task.CompletedTask;

        PluginInfo? info = pluginManager.GetPluginInfo(pluginId);

        if (info is null)
            return Task.CompletedTask;

        if (registrar.Attach(info, pluginManager))
        {
            logger.LogInformation(
                "Plugin {PluginName} is now serving its own endpoints.",
                info.Name
            );
        }

        return Task.CompletedTask;
    }

    private Task OnDisabled(PluginDisabledEvent disabled, CancellationToken ct)
    {
        if (Ulid.TryParse(disabled.PluginId, out Ulid pluginId))
            registrar.Detach(pluginId);

        return Task.CompletedTask;
    }
}
