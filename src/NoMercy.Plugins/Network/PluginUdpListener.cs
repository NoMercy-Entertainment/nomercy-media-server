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
/// A bound datagram socket.
/// <para>
/// There is one stream however many peers write to it, because a datagram
/// socket has one endpoint. Accepting twice hands back the same stream rather
/// than pretending each peer has a connection of its own.
/// </para>
/// </summary>
public sealed class PluginUdpListener : IPluginListener
{
    private readonly UdpClient _client;
    private readonly PluginDatagramStream _stream;

    public PluginUdpListener(int port)
        : this(IPAddress.Any, port) { }

    /// <summary>
    /// Binds a chosen address. The server passes the wildcard so a plugin can
    /// serve the LAN; tests pass loopback so a run never asks the firewall.
    /// </summary>
    public PluginUdpListener(IPAddress bindAddress, int port)
    {
        _client = new(new IPEndPoint(bindAddress, port));
        Port = ((IPEndPoint)_client.Client.LocalEndPoint!).Port;
        _stream = new(_client);
    }

    public int Port { get; }

    public Task<Stream> AcceptAsync(CancellationToken ct = default) =>
        Task.FromResult<Stream>(_stream);

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _client.Dispose();
    }
}
