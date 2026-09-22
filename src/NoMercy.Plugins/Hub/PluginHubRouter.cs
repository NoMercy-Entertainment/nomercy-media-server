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
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;

namespace NoMercy.PluginSdk.Hub;

public class PluginHubRouter(Func<IPluginManager> pluginManager, ILogger<PluginHubRouter> logger)
    : IPluginHubRouter
{
    // The manager is built with the context factory, the context factory
    // needs the hub context factory, and that needs this router: taking the
    // manager at construction closes a ring the container cannot resolve.
    // It is only consulted when a message arrives, so it is looked up then.
    public PluginHubRouter(IPluginManager pluginManager, ILogger<PluginHubRouter> logger)
        : this(() => pluginManager, logger) { }

    private readonly ConcurrentDictionary<Ulid, IPluginHubHandler> _handlers = new();

    public void Register(IPluginHubHandler handler) => _handlers[handler.PluginId] = handler;

    private readonly ConcurrentDictionary<Ulid, PluginDelegateHubHandler> _delegates = new();

    public void Unregister(Ulid pluginId)
    {
        _handlers.TryRemove(pluginId, out _);
        _delegates.TryRemove(pluginId, out _);
    }

    public PluginDelegateHubHandler DelegateHandlerFor(Ulid pluginId) =>
        _delegates.GetOrAdd(pluginId, static id => new(id));

    public async Task<bool> RouteAsync(
        Ulid pluginId,
        PluginHubMessage message,
        IPluginHubClient client,
        CancellationToken ct
    )
    {
        _handlers.TryGetValue(pluginId, out IPluginHubHandler? handler);
        _delegates.TryGetValue(pluginId, out PluginDelegateHubHandler? delegateHandler);

        if (handler is null && delegateHandler is null)
            return false;

        PluginInfo? info = pluginManager().GetPluginInfo(pluginId);

        if (info is null || info.Status != PluginStatus.Active)
            return false;

        if (info.Capabilities?.Ws != true)
            return false;

        try
        {
            if (handler is not null)
                await handler.HandleAsync(message, client, ct);

            if (delegateHandler is not null)
                await delegateHandler.HandleAsync(message, client, ct);

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
