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
using Microsoft.Extensions.Logging;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;

namespace NoMercy.Plugins.Hub;

public class PluginHubRouter(IPluginManager pluginManager, ILogger<PluginHubRouter> logger)
    : IPluginHubRouter
{
    private readonly ConcurrentDictionary<Ulid, IPluginHubHandler> _handlers = new();

    public void Register(IPluginHubHandler handler) => _handlers[handler.PluginId] = handler;

    public void Unregister(Ulid pluginId) => _handlers.TryRemove(pluginId, out _);

    public async Task<bool> RouteAsync(
        Ulid pluginId,
        PluginHubMessage message,
        IPluginHubClient client,
        CancellationToken ct
    )
    {
        if (!_handlers.TryGetValue(pluginId, out IPluginHubHandler? handler))
            return false;

        PluginInfo? info = pluginManager.GetPluginInfo(pluginId);

        if (info is null || info.Status != PluginStatus.Active)
            return false;

        if (info.Capabilities?.Ws != true)
            return false;

        try
        {
            await handler.HandleAsync(message, client, ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller hung up mid-handler. Not the plugin's fault and not
            // worth an error line on every page navigation.
            return false;
        }
        catch (Exception exception)
        {
            // A throwing plugin must not take the hub connection down with it;
            // every other plugin is multiplexed over the same one.
            if (PluginStaleMemberLog.Explain(logger, pluginId, exception, "handling a hub message"))
                return false;

            logger.LogError(
                exception,
                "Plugin {PluginId} threw handling hub method {Method}.",
                pluginId,
                message.Method
            );
            return false;
        }
    }
}
