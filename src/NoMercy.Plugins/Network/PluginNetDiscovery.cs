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

using System.Runtime.CompilerServices;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;
using NoMercy.Plugins.Runtime;

namespace NoMercy.Plugins.Network;

/// <summary>
/// Finding peers and devices on the owner's own network.
/// <para>
/// Its own capability rather than a rider on dialling, because this
/// enumerates machines the owner never mentioned to the server: what is in
/// their house, and when it is switched on. A plugin allowed to reach one
/// tracker is not thereby allowed to inventory a home.
/// </para>
/// </summary>
public class PluginNetDiscovery(
    Ulid pluginId,
    IPluginCapabilityBroker broker,
    IPluginServiceDiscoveryClient client,
    IPluginResourceLedger ledger
) : IPluginNetDiscovery
{
    internal IPluginResourceLedger Ledger => ledger;

    public async IAsyncEnumerable<PluginDiscoveredService> BrowseAsync(
        string protocol,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        // Before the client, not inside it: a refusal that arrives after the
        // first multicast has gone out is a refusal that already leaked which
        // service the plugin was looking for.
        Refuse(protocol);

        await foreach (PluginDiscoveredService service in client.BrowseAsync(protocol, ct))
            yield return service;
    }

    public async Task<IAsyncDisposable> AnnounceAsync(
        string protocol,
        string instance,
        int port,
        IReadOnlyDictionary<string, string>? attributes = null,
        CancellationToken ct = default
    )
    {
        Refuse(protocol);

        await client.AnnounceAsync(protocol, instance, port, attributes, ct);

        PluginAnnouncement announcement = new(client, protocol, instance, ledger, pluginId);
        ledger.Track(pluginId, announcement);

        return announcement;
    }

    /// <summary>
    /// The broker's own answer, passed through rather than rewritten: it
    /// already tells the three apart — never declared, declared and not
    /// approved, approved but not for this service type — and each has a
    /// different fix.
    /// </summary>
    private void Refuse(string protocol)
    {
        if (broker.Check(pluginId, PluginCapabilityNames.NetworkDiscover, protocol) is { } refusal)
            throw new PluginRefusedException(refusal);
    }
}

/// <summary>
/// A live announcement. Disposing it stops advertising without stopping the
/// plugin, and the ledger disposes any the plugin forgot.
/// </summary>
internal sealed class PluginAnnouncement(
    IPluginServiceDiscoveryClient client,
    string protocol,
    string instance,
    IPluginResourceLedger ledger,
    Ulid pluginId
) : IAsyncDisposable
{
    private bool _stopped;

    public async ValueTask DisposeAsync()
    {
        if (_stopped)
            return;

        _stopped = true;

        // Handed back first, so a plugin that tidies up properly does not
        // leave an entry the ledger will try to dispose a second time.
        ledger.Forget(pluginId, this);

        await client.StopAsync(protocol, instance);
    }
}
