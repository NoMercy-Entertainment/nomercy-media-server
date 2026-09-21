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
using NoMercy.Plugins.Abstractions;

namespace NoMercy.PluginHost;

/// <summary>
/// Each facade, as the plugin process sees it: the same interface the contract
/// published, with the body replaced by one named call over the channel.
/// <para>
/// The facade name on the wire is the lower-case member name on
/// <see cref="IPluginContext" />, so the broker can route without a table that
/// two sides have to keep in step.
/// </para>
/// </summary>
internal sealed class RemoteSecrets(RemoteCall call) : IPluginSecretStore
{
    public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        call.AskAsync<string?>("secrets", nameof(GetAsync), new { key });

    public Task SetAsync(string key, string value, CancellationToken ct = default) =>
        call.TellAsync("secrets", nameof(SetAsync), new { key, value });

    public Task DeleteAsync(string key, CancellationToken ct = default) =>
        call.TellAsync("secrets", nameof(DeleteAsync), new { key });

    public async Task<IReadOnlyList<string>> KeysAsync(CancellationToken ct = default) =>
        await call.AskAsync<List<string>>("secrets", nameof(KeysAsync)) ?? [];

    public Task<string?> GetForUserAsync(string key, CancellationToken ct = default) =>
        call.AskAsync<string?>("secrets", nameof(GetForUserAsync), new { key });

    public Task SetForUserAsync(string key, string value, CancellationToken ct = default) =>
        call.TellAsync("secrets", nameof(SetForUserAsync), new { key, value });

    public Task DeleteForUserAsync(string key, CancellationToken ct = default) =>
        call.TellAsync("secrets", nameof(DeleteForUserAsync), new { key });
}

internal sealed class RemoteGrants(RemoteCall call) : IPluginGrants
{
    public Task<bool> HasAsync(string kind, string value, CancellationToken ct = default) =>
        call.AskAsync<bool>("grants", nameof(HasAsync), new { kind, value });

    public async Task<IReadOnlyList<string>> GetAsync(
        string kind,
        CancellationToken ct = default
    ) => await call.AskAsync<List<string>>("grants", nameof(GetAsync), new { kind }) ?? [];

    public Task RequestAsync(
        string kind,
        string value,
        string reason,
        CancellationToken ct = default
    ) =>
        call.TellAsync(
            "grants",
            nameof(RequestAsync),
            new
            {
                kind,
                value,
                reason,
            }
        );
}

internal sealed class RemoteHub(RemoteCall call) : IPluginHubContext
{
    public Task PushAsync(string type, object? payload) =>
        call.TellAsync("hub", nameof(PushAsync), new { type, payload });

    public Task PushToUserAsync(string userId, string type, object? payload) =>
        call.TellAsync(
            "hub",
            nameof(PushToUserAsync),
            new
            {
                userId,
                type,
                payload,
            }
        );

    /// <summary>
    /// The handler lives in this process; what crosses the channel is only the
    /// fact that this method now has one, so the server can route a client's
    /// call to it. Registering it on the far side is impossible: the delegate
    /// closes over the plugin's own state, which the server cannot hold.
    /// </summary>
    public void Handle(
        string method,
        Func<PluginCaller, JsonNode?, CancellationToken, Task<object?>> handler
    )
    {
        lock (_handlers)
            _handlers[method] = handler;

        call.Tell("hub", nameof(Handle), new { method });
    }

    private readonly Dictionary<
        string,
        Func<PluginCaller, JsonNode?, CancellationToken, Task<object?>>
    > _handlers = new();

    /// <summary>Invoked by the host when a client calls a handled method.</summary>
    public async Task<object?> InvokeAsync(
        string method,
        PluginCaller caller,
        JsonNode? payload,
        CancellationToken ct
    )
    {
        Func<PluginCaller, JsonNode?, CancellationToken, Task<object?>>? handler;

        lock (_handlers)
            _handlers.TryGetValue(method, out handler);

        return handler is null ? null : await handler(caller, payload, ct);
    }
}

/// <summary>
/// Events are the one facade that is not a straight relay.
/// <para>
/// A subscription registers a handler that lives in this process, so what
/// crosses the channel is the fact of the subscription; the delivery comes
/// back the other way and is dispatched here. Until the broker pushes
/// deliveries (Task 4) a handler is kept rather than dropped, so a plugin that
/// subscribed at startup is still subscribed when it starts arriving.
/// </para>
/// </summary>
internal sealed class RemoteEvents(RemoteCall call) : IPluginEvents
{
    private readonly Dictionary<string, List<Func<string, CancellationToken, Task>>> _handlers =
        new();

    public void Subscribe<T>(string topic, Func<T, CancellationToken, Task> handler)
    {
        lock (_handlers)
        {
            if (
                !_handlers.TryGetValue(topic, out List<Func<string, CancellationToken, Task>>? list)
            )
                _handlers[topic] = list = [];

            list.Add(
                (payload, ct) =>
                    handler(
                        System.Text.Json.JsonSerializer.Deserialize<T>(
                            payload,
                            new System.Text.Json.JsonSerializerOptions(
                                System.Text.Json.JsonSerializerDefaults.Web
                            )
                        )!,
                        ct
                    )
            );
        }

        call.Tell("events", nameof(Subscribe), new { topic });
    }

    public Task PublishAsync<T>(string name, T payload, CancellationToken ct = default) =>
        call.TellAsync("events", nameof(PublishAsync), new { name, payload });

    /// <summary>Called by the host when the broker delivers a topic.</summary>
    public async Task DeliverAsync(string topic, string payloadJson, CancellationToken ct)
    {
        Func<string, CancellationToken, Task>[] handlers;

        lock (_handlers)
        {
            if (
                !_handlers.TryGetValue(topic, out List<Func<string, CancellationToken, Task>>? list)
            )
                return;

            handlers = [.. list];
        }

        foreach (Func<string, CancellationToken, Task> handler in handlers)
            await handler(payloadJson, ct);
    }
}
