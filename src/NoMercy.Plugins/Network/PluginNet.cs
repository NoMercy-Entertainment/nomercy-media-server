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

using System.Net.Sockets;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;
using NoMercy.Plugins.Runtime;

namespace NoMercy.Plugins.Network;

/// <summary>
/// Sockets the owner consented to.
/// <para>
/// Three different things can be wrong, and the refusal says which: the
/// capability was never declared or approved, the host is outside the globs
/// the manifest named, or the port is outside the range it named. Collapsed
/// into one message, an author edits a manifest line that was already right.
/// </para>
/// </summary>
public class PluginNet(
    Ulid pluginId,
    IPluginCapabilityBroker broker,
    IPluginManifestSource manifests,
    IPluginResourceLedger ledger
) : IPluginNet
{
    public async Task<Stream> DialAsync(
        string host,
        int port,
        PluginTransport transport,
        CancellationToken ct = default
    )
    {
        // Asked twice, because the two answers have different fixes. Without
        // the scope the question is only "may this plugin dial at all"; with
        // it, "and may it dial there".
        if (broker.Check(pluginId, PluginCapabilityNames.NetworkDial) is not null)
            throw new PluginRefusedException(
                PluginRefusalMessages.SocketUndeclared(pluginId.ToString(), host, port)
            );

        if (broker.Check(pluginId, PluginCapabilityNames.NetworkDial, host) is not null)
            throw new PluginRefusedException(
                PluginRefusalMessages.HostNotAllowed(pluginId.ToString(), host)
            );

        if (transport == PluginTransport.Udp)
        {
            UdpClient client = new();
            client.Connect(host, port);

            return new PluginDatagramStream(client);
        }

        TcpClient tcp = new();
        await tcp.ConnectAsync(host, port, ct);

        return tcp.GetStream();
    }

    public Task<IPluginListener> ListenAsync(
        int port,
        PluginTransport transport,
        CancellationToken ct = default
    )
    {
        if (broker.Check(pluginId, PluginCapabilityNames.NetworkListen) is not null)
            throw new PluginRefusedException(
                PluginRefusalMessages.ListenerUndeclared(pluginId.ToString(), port)
            );

        IReadOnlyList<string> declared =
            manifests.Find(pluginId)?.Capabilities?.Network?.Ports ?? [];

        // Port zero is the plugin asking the host to pick, so there is nothing
        // to check yet. What it got is checked below, against the same list.
        if (port != 0 && !PluginPortRange.Contains(declared, port))
            throw new PluginRefusedException(
                PluginRefusalMessages.ListenerUndeclared(pluginId.ToString(), port)
            );

        IPluginListener listener =
            transport == PluginTransport.Udp
                ? new PluginUdpListener(port)
                : new PluginTcpListener(port);

        if (!PluginPortRange.Contains(declared, listener.Port))
        {
            listener.DisposeAsync().AsTask().GetAwaiter().GetResult();

            throw new PluginRefusedException(
                PluginRefusalMessages.ListenerUndeclared(pluginId.ToString(), listener.Port)
            );
        }

        // Tracked so the port goes back when the plugin stops, however it
        // stops. A listener a crashed plugin left bound is one the owner can
        // only clear by restarting the server.
        ledger.Track(pluginId, listener);

        return Task.FromResult(listener);
    }

    /// <summary>Built in Phase 2 task 29; not reachable from here yet.</summary>
    public IPluginNetDiscovery Discovery =>
        throw new PluginRefusedException(
            PluginRefusalMessages.FacadeNotOnThisHost(pluginId.ToString(), "IPluginNet.Discovery")
        );

    /// <summary>Built in Phase 2 task 29; not reachable from here yet.</summary>
    public IPluginPortMap PortMap =>
        throw new PluginRefusedException(
            PluginRefusalMessages.FacadeNotOnThisHost(pluginId.ToString(), "IPluginNet.PortMap")
        );
}
