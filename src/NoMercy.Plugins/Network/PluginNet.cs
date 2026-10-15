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

using System.Net;
using System.Net.Sockets;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Runtime;

namespace NoMercy.PluginSdk.Network;

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
    IPluginResourceLedger ledger,
    IPluginNetDiscovery? discovery = null,
    IPluginPortMap? portMap = null,
    IPAddress? bindAddress = null
) : IPluginNet
{
    /// <summary>
    /// The address every listener this plugin opens binds. The server leaves it
    /// at the wildcard so a plugin can serve the LAN; tests pass loopback so a
    /// test run never asks the Windows firewall.
    /// </summary>
    public IPAddress BindAddress { get; } = bindAddress ?? IPAddress.Any;

    /// <summary>How a TCP client is made, so a test can see it disposed.</summary>
    internal Func<TcpClient> NewTcpClient { get; init; } = () => new TcpClient();

    /// <summary>How a UDP client is made, so a test can see it disposed.</summary>
    internal Func<UdpClient> NewUdpClient { get; init; } = () => new UdpClient();

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

        // Disposed on any failure before the stream is handed over. A refused,
        // unreachable or cancelled connect otherwise kept its socket until the
        // finalizer ran, and a plugin retrying in a loop held one per attempt.
        if (transport == PluginTransport.Udp)
        {
            UdpClient client = NewUdpClient();

            try
            {
                client.Connect(host, port);

                return new PluginDatagramStream(client);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        TcpClient tcp = NewTcpClient();

        try
        {
            await tcp.ConnectAsync(host, port, ct);

            return tcp.GetStream();
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
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

        // Port zero is the plugin asking the host to pick one inside its range.
        if (port != 0 && !PluginPortRange.Contains(declared, port))
            throw new PluginRefusedException(
                PluginRefusalMessages.ListenerUndeclared(pluginId.ToString(), port)
            );

        IPluginListener listener =
            port == 0 ? BindInRange(declared, transport) : Bind(transport, port);

        // Said again for every listener, however it was picked: the range is
        // the owner's grant, and a port outside it is never handed back.
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

    private IPluginListener Bind(PluginTransport transport, int port) =>
        transport == PluginTransport.Udp
            ? new PluginUdpListener(BindAddress, port)
            : new PluginTcpListener(BindAddress, port);

    /// <summary>
    /// A free port inside the declared range. The operating system's own pick
    /// first, which inside a wide range is all it takes; then each declared
    /// port in turn. The system picks from its ephemeral ports, which a narrow
    /// range such as 6881-6889 almost never contains, so leaving the pick to
    /// it alone refused every such plugin.
    /// </summary>
    private IPluginListener BindInRange(IReadOnlyList<string> declared, PluginTransport transport)
    {
        IPluginListener picked = Bind(transport, 0);

        if (PluginPortRange.Contains(declared, picked.Port))
            return picked;

        picked.DisposeAsync().AsTask().GetAwaiter().GetResult();

        foreach (int candidate in PluginPortRange.Ports(declared))
        {
            try
            {
                return Bind(transport, candidate);
            }
            catch (SocketException)
            {
                // Taken. The next one may not be.
            }
        }

        throw new PluginRefusedException(
            PluginRefusalMessages.NoFreePortInRange(
                pluginId.ToString(),
                string.Join(", ", declared)
            )
        );
    }

    /// <summary>
    /// Finding peers and devices on the owner's network. Refuses by name on a
    /// host that wired no discovery client, rather than answering an empty
    /// stream a plugin would read as "nothing out there".
    /// </summary>
    public IPluginNetDiscovery Discovery =>
        discovery
        ?? throw new PluginRefusedException(
            PluginRefusalMessages.FacadeNotOnThisHost(pluginId.ToString(), "IPluginNet.Discovery")
        );

    /// <summary>
    /// Asking the router to forward a port. Refuses by name where no router
    /// client is wired, so a plugin learns its port is not forwarded instead
    /// of advertising one that is not.
    /// </summary>
    public IPluginPortMap PortMap =>
        portMap
        ?? throw new PluginRefusedException(
            PluginRefusalMessages.FacadeNotOnThisHost(pluginId.ToString(), "IPluginNet.PortMap")
        );
}
