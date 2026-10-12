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

using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using NoMercy.Networking.Certificate;
using NoMercy.NmSystem.Networking;

namespace NoMercy.Networking.Connectivity;

/// <summary>
/// Answers the reachability call-back before Kestrel owns the port. The server must know
/// whether its port answers from outside before it starts serving, so the first transport
/// it picks is the right one; at that moment nothing listens yet. This listener holds the
/// port only for the length of the check and answers nothing but a live code.
/// </summary>
public sealed class ReachListener : IAsyncDisposable
{
    private static readonly byte[] NotFound = Encoding.ASCII.GetBytes(
        "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
    );

    private readonly List<TcpListener> _listeners;
    private readonly ReachCodes _codes;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _loops;

    private ReachListener(List<TcpListener> listeners, ReachCodes codes)
    {
        _listeners = listeners;
        _codes = codes;
        _loops = listeners.Select(AcceptLoopAsync).ToList();
    }

    /// <summary>The addresses Kestrel listens on: every IPv4 and every IPv6 address.</summary>
    public static readonly IReadOnlyList<IPAddress> AllAddresses = [IPAddress.Any, IPAddress.IPv6Any];

    /// <summary>
    /// Binds <paramref name="port"/> on <paramref name="addresses"/> (default
    /// <see cref="AllAddresses"/>). Null when no IPv4 address could be bound: something else
    /// holds the port, and a call-back would reach that instead.
    /// </summary>
    public static ReachListener? TryStart(
        int port,
        ReachCodes codes,
        ILogger logger,
        IReadOnlyList<IPAddress>? addresses = null
    )
    {
        List<TcpListener> bound = [];

        foreach (IPAddress address in addresses ?? AllAddresses)
        {
            TcpListener listener = new(address, port);
            try
            {
                // IPv6-only, so it never collides with the IPv4 socket bound next to it.
                if (address.AddressFamily == AddressFamily.InterNetworkV6)
                    listener.Server.DualMode = false;
                listener.Start();
                bound.Add(listener);
            }
            catch (SocketException ex)
            {
                logger.LogDebug(
                    "Reachability listener could not bind {Address} port {Port}: {Message}",
                    address,
                    port,
                    ex.Message
                );
                listener.Server.Dispose();
            }
        }

        bool ipv4Bound = bound.Any(l => l.LocalEndpoint.AddressFamily == AddressFamily.InterNetwork);
        if (!ipv4Bound)
        {
            // IPv4 is the family that decides a port forward. Without it the check would only
            // reach whatever else holds the port.
            foreach (TcpListener listener in bound)
                listener.Stop();
            return null;
        }

        return new(bound, codes);
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            _ = AnswerAsync(client);
        }
    }

    private async Task AnswerAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
                    _stop.Token
                );
                timeout.CancelAfter(TimeSpan.FromSeconds(5));

                NetworkStream stream = client.GetStream();
                byte[] buffer = new byte[2048];
                int read = 0;
                while (read < buffer.Length)
                {
                    int n = await stream.ReadAsync(buffer.AsMemory(read), timeout.Token);
                    if (n == 0)
                        break;
                    read += n;
                    // The request line is all the answer depends on.
                    if (buffer.AsSpan(0, read).IndexOf("\r\n"u8) >= 0)
                        break;
                }

                string path = PlaintextToHttpsRedirector.ParseRequestPath(
                    new ReadOnlySequence<byte>(buffer, 0, read)
                );
                string? code = _codes.Match(path);

                await stream.WriteAsync(
                    code is null ? NotFound : ReachCodes.HttpAnswer(code),
                    timeout.Token
                );
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                // A caller that hangs up or stalls gets nothing; the check it was part of
                // reports what it saw on its own side.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        foreach (TcpListener listener in _listeners)
            listener.Stop();
        await Task.WhenAll(_loops);
        _stop.Dispose();
    }
}
