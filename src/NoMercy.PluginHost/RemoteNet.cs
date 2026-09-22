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
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginHost;

/// <summary>
/// Sockets the server permitted and the plugin opens.
/// <para>
/// The bytes do not cross the channel. A download proxied through the server
/// would be copied twice and would put the server in the middle of a
/// connection it has no reason to read.
/// </para>
/// <para>
/// The server is asked first and every time, never once at startup: the owner
/// can withdraw a host while the plugin is running, and a plugin that cached
/// permission would keep reaching a host the owner has since refused.
/// </para>
/// </summary>
public sealed class RemoteNet(Ulid pluginId, RemoteCall call) : IPluginNet
{
    public async Task<Stream> DialAsync(
        string host,
        int port,
        PluginTransport transport,
        CancellationToken ct = default
    )
    {
        // The refusal arrives as an exception from here, so reaching the next
        // line at all is the server's yes.
        await call.AskAsync<bool>("net", nameof(IPluginNet.DialAsync), new { host, port });

        if (transport == PluginTransport.Udp)
        {
            UdpClient datagram = new();
            datagram.Connect(host, port);

            return new PluginDatagramStream(datagram);
        }

        TcpClient tcp = new();
        await tcp.ConnectAsync(host, port, ct);

        return tcp.GetStream();
    }

    /// <summary>
    /// Listening, discovery and port mapping cross the boundary in their own
    /// task. A facade that answered a listener nobody could reach would look
    /// to a plugin like a port the owner's router had closed.
    /// </summary>
    public Task<IPluginListener> ListenAsync(
        int port,
        PluginTransport transport,
        CancellationToken ct = default
    ) => throw new PluginRefusedException(NotYet(nameof(IPluginNet.ListenAsync)));

    public IPluginNetDiscovery Discovery =>
        throw new PluginRefusedException(NotYet(nameof(IPluginNet.Discovery)));

    public IPluginPortMap PortMap =>
        throw new PluginRefusedException(NotYet(nameof(IPluginNet.PortMap)));

    private PluginRefusal NotYet(string member) =>
        new(
            PluginRefusalCodes.HostServicesRemoved,
            pluginId.ToString(),
            $"A plugin in its own process asked for net.{member}.",
            "This server runs the plugin out of process, and that member does not cross the boundary yet.",
            "Run this plugin in the server's own process until it is carried across. Docs: /nomercy-plugins/handbook/runtime-and-isolation",
            PluginRefusalSeverity.Blocked
        );
}
