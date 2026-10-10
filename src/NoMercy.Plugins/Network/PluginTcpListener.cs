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

namespace NoMercy.PluginSdk.Network;

/// <summary>
/// A bound TCP socket. <see cref="Port" /> is read from the endpoint the
/// socket actually got, which is the only answer that is true when the plugin
/// asked for port zero.
/// </summary>
public sealed class PluginTcpListener : IPluginListener
{
    private readonly TcpListener _listener;

    public PluginTcpListener(int port)
        : this(IPAddress.Any, port) { }

    /// <summary>
    /// Binds a chosen address. The server passes the wildcard so a plugin can
    /// serve the LAN; tests pass loopback so a run never asks the firewall.
    /// </summary>
    public PluginTcpListener(IPAddress bindAddress, int port)
    {
        _listener = new(bindAddress, port);
        _listener.Start();

        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    public async Task<Stream> AcceptAsync(CancellationToken ct = default)
    {
        TcpClient client = await _listener.AcceptTcpClientAsync(ct);

        return client.GetStream();
    }

    public ValueTask DisposeAsync()
    {
        _listener.Stop();
        _listener.Dispose();

        return ValueTask.CompletedTask;
    }
}
