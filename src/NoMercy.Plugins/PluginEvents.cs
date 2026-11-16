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

using System.Text.Json;
using Microsoft.Extensions.Logging;
using NoMercy.Events;
using NoMercy.Events.Plugins;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginSdk;

/// <summary>
/// The topic facade over the host bus. A plugin names a topic and gets the
/// payload it expects; it never holds the bus, so it never sees the events it
/// has no capability for.
/// </summary>
public sealed class PluginEvents(Ulid pluginId, IEventBus eventBus, ILogger? logger = null)
    : IPluginEvents,
        IDisposable
{
    private readonly List<IDisposable> _subscriptions = [];

    public void Subscribe<T>(string topic, Func<T, CancellationToken, Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentNullException.ThrowIfNull(handler);

        _subscriptions.Add(
            eventBus.Subscribe<PluginMessageEvent>(
                (message, ct) =>
                {
                    if (message.Name != topic)
                    {
                        return Task.CompletedTask;
                    }

                    // A host topic is the server's word: one that arrives under a
                    // plugin's id was forged on the bus.
                    if (
                        PluginHostTopics.IsReserved(topic)
                        && message.PluginId != PluginHostTopics.HostSender
                    )
                    {
                        return Task.CompletedTask;
                    }

                    T? payload = message.Payload is null
                        ? default
                        : message.Payload.Deserialize<T>(JsonSerializerOptions.Web);

                    return payload is null ? Task.CompletedTask : handler(payload, ct);
                }
            )
        );
    }

    public Task PublishAsync<T>(string name, T payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (PluginHostTopics.IsReserved(name))
        {
            logger?.LogWarning(
                "Plugin {PluginId} tried to publish the host topic {Topic}; refused",
                pluginId,
                name
            );
            throw new InvalidOperationException(
                $"The topic '{name}' is published by the server only. A plugin may subscribe to it and may not publish it; publish under a topic name of its own."
            );
        }

        return eventBus.PublishAsync(PluginMessageEvent.From(pluginId, name, payload), ct);
    }

    public void Dispose()
    {
        foreach (IDisposable subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
    }
}
