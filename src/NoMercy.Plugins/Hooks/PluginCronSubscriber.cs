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

using Microsoft.Extensions.Hosting;
using NoMercy.Events;
using NoMercy.Events.Plugins;

namespace NoMercy.PluginSdk.Hooks;

/// <summary>
/// Keeps a plugin's scheduled jobs matching whether the plugin is running.
/// <para>
/// The boot pass registers what it loaded, but a plugin also starts running
/// later: a late load during boot, an install, an approval, a restart. Each of
/// those announces itself with <see cref="PluginLoadedEvent"/>, the same event
/// the route subscriber attaches controllers on, so the jobs follow it here.
/// The registrar ignores a second sighting of the same instance, so a plugin
/// both paths see is scheduled once.
/// </para>
/// </summary>
public class PluginCronSubscriber(IEventBus eventBus, IPluginCronRegistrar registrar)
    : IHostedService
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
        if (Ulid.TryParse(loaded.PluginId, out Ulid pluginId))
            registrar.RegisterPlugin(pluginId);

        return Task.CompletedTask;
    }

    private Task OnDisabled(PluginDisabledEvent disabled, CancellationToken ct)
    {
        if (Ulid.TryParse(disabled.PluginId, out Ulid pluginId))
            registrar.UnregisterPlugin(pluginId);

        return Task.CompletedTask;
    }
}
