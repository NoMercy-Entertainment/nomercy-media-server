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

using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NoMercy.Plugins.Network;

/// <summary>
/// Keeps every plugin's router mappings alive.
/// <para>
/// A NAT-PMP lease is minutes, not hours: nothing renewing it means a plugin
/// that was reachable when the owner set it up is quietly unreachable an hour
/// later, with no error anywhere. Renewal is the host's job because the plugin
/// asking for a mapping has no reason to know that.
/// </para>
/// </summary>
public class PluginPortMapRenewalService(ILogger<PluginPortMapRenewalService> logger)
    : BackgroundService
{
    private readonly ConcurrentDictionary<Ulid, PluginPortMap> _maps = new();

    public void Track(Ulid pluginId, PluginPortMap map) => _maps[pluginId] = map;

    public void Forget(Ulid pluginId) => _maps.TryRemove(pluginId, out _);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A minute is well inside the shortest lease a plugin can reasonably
        // ask for, and the pass costs nothing when nothing is due.
        using PeriodicTimer timer = new(TimeSpan.FromMinutes(1));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach ((Ulid pluginId, PluginPortMap map) in _maps)
            {
                try
                {
                    await map.RenewDueAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // One router that will not answer must not stop the next
                    // plugin's mappings from being renewed.
                    logger.LogWarning(
                        ex,
                        "Renewing the router mappings for plugin {PluginId} failed: {Error}",
                        pluginId,
                        ex.Message
                    );
                }
            }
        }
    }
}
