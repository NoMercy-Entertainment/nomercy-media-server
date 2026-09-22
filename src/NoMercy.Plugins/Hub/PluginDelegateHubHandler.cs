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
using System.Text.Json.Nodes;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginSdk.Hub;

/// <summary>
/// The handler behind <see cref="IPluginHubContext.Handle" />.
/// <para>
/// A plugin registers one delegate per method name instead of implementing
/// <see cref="IPluginHubHandler" /> and switching on the method itself. Both
/// arrive at the router the same way, so a plugin can use either and a plugin
/// using neither is unaffected.
/// </para>
/// </summary>
public sealed class PluginDelegateHubHandler(Ulid pluginId) : IPluginHubHandler
{
    private readonly ConcurrentDictionary<
        string,
        Func<PluginCaller, JsonNode?, CancellationToken, Task<object?>>
    > _methods = new(StringComparer.Ordinal);

    public Ulid PluginId => pluginId;

    /// <summary>Last registration wins, so a reload replaces rather than doubles.</summary>
    public void Register(
        string method,
        Func<PluginCaller, JsonNode?, CancellationToken, Task<object?>> handler
    ) => _methods[method] = handler;

    public async Task HandleAsync(
        PluginHubMessage message,
        IPluginHubClient client,
        CancellationToken ct
    )
    {
        if (!_methods.TryGetValue(message.Method, out var handler))
            return;

        // No caller, no dispatch. A hub method that runs without knowing who
        // asked cannot tell the owner from a guest, and the plugin has no way
        // to find out afterwards.
        if (message.Caller is null)
            throw new PluginRefusedException(
                PluginRefusalMessages.HubCallerNotResolved(pluginId.ToString(), message.Method)
            );

        object? result = await handler(message.Caller, message.Payload, ct);

        if (result is not null)
            await client.SendAsync(message.Method, result);
    }
}
