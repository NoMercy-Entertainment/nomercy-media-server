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

using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;
using NoMercy.Plugins.Runtime;

namespace NoMercy.Plugins.Network;

/// <summary>
/// Asking the router to forward a port.
/// <para>
/// This is the one thing here that outlives the process: a mapping the plugin
/// never drops is a hole in the owner's router that nothing closes. Every
/// mapping goes in the resource ledger, so stopping the plugin drops it at
/// the router too.
/// </para>
/// <para>
/// Travels with <c>network.listen</c>, because a listener nobody outside can
/// reach is the same plugin failing quietly rather than loudly.
/// </para>
/// </summary>
public class PluginPortMap(
    Ulid pluginId,
    IPluginCapabilityBroker broker,
    IPluginPortMapClient client,
    IPluginResourceLedger ledger,
    TimeProvider clock
) : IPluginPortMap
{
    private readonly List<Held> _held = [];
    private readonly Lock _gate = new();

    public async Task<PluginPortMapping> MapAsync(
        int internalPort,
        int externalPort,
        PluginTransport transport,
        TimeSpan lease,
        CancellationToken ct = default
    )
    {
        if (broker.Check(pluginId, PluginCapabilityNames.NetworkListen) is not null)
            throw new PluginRefusedException(
                PluginRefusalMessages.ListenerUndeclared(pluginId.ToString(), internalPort)
            );

        // The router grants the port it chooses, which is not always the one
        // asked for. Answering the request rather than the grant is how a
        // plugin advertises a port nothing is forwarded to.
        int granted = await client.MapAsync(internalPort, externalPort, transport, lease, ct);

        PluginPortMapping mapping = new(
            internalPort,
            granted,
            transport,
            clock.GetUtcNow().Add(lease)
        );
        Held held = new(mapping, lease);

        lock (_gate)
            _held.Add(held);

        ledger.Track(pluginId, new PluginPortMapHandle(this, held));

        return mapping;
    }

    public Task UnmapAsync(PluginPortMapping mapping, CancellationToken ct = default) =>
        DropAsync(Find(mapping), ct);

    public Task<IReadOnlyList<PluginPortMapping>> ListAsync(CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<PluginPortMapping>>([
                .. _held.Select(entry => entry.Mapping),
            ]);
    }

    /// <summary>
    /// Renews every lease more than half spent.
    /// <para>
    /// Driven by a clock rather than a timer inside the facade, so a lease
    /// that lapses in production lapses in a test in one line.
    /// </para>
    /// </summary>
    public async Task RenewDueAsync(CancellationToken ct = default)
    {
        Held[] due;
        DateTimeOffset now = clock.GetUtcNow();

        lock (_gate)
            due = [.. _held.Where(entry => now >= entry.Mapping.ExpiresAt - (entry.Lease / 2))];

        foreach (Held entry in due)
        {
            int granted = await client.MapAsync(
                entry.Mapping.InternalPort,
                entry.Mapping.ExternalPort,
                entry.Mapping.Transport,
                entry.Lease,
                ct
            );

            entry.Mapping = entry.Mapping with
            {
                ExternalPort = granted,
                ExpiresAt = clock.GetUtcNow().Add(entry.Lease),
            };
        }
    }

    private Held? Find(PluginPortMapping mapping)
    {
        lock (_gate)
            return _held.FirstOrDefault(entry =>
                entry.Mapping.InternalPort == mapping.InternalPort
                && entry.Mapping.Transport == mapping.Transport
            );
    }

    private async Task DropAsync(Held? held, CancellationToken ct)
    {
        if (held is null)
            return;

        // Dropped once, whoever asks. A plugin that unmaps and then stops
        // would otherwise tell the router twice, and the second call names a
        // mapping that is no longer there.
        lock (_gate)
        {
            if (held.Dropped)
                return;

            held.Dropped = true;
            _held.Remove(held);
        }

        await client.UnmapAsync(held.Mapping, ct);
    }

    /// <summary>One mapping and the lease it was granted on, which renewal needs.</summary>
    private sealed class Held(PluginPortMapping mapping, TimeSpan lease)
    {
        public PluginPortMapping Mapping { get; set; } = mapping;

        public TimeSpan Lease { get; } = lease;

        public bool Dropped { get; set; }
    }

    /// <summary>
    /// What the ledger holds. Disposing it drops the mapping at the router,
    /// which is what makes a stopped plugin stop holding the owner's router
    /// open.
    /// </summary>
    private sealed class PluginPortMapHandle(PluginPortMap map, Held held) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await map.DropAsync(held, CancellationToken.None);
    }
}
