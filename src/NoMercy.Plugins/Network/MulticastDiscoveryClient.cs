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

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginSdk.Network;

/// <summary>
/// DNS-SD over multicast DNS: the way a torrent client, a printer and a
/// television all announce themselves on a home network.
/// <para>
/// Browsing is a running conversation rather than a question with an answer,
/// so it streams until it is cancelled. Announcing repeats, because a device
/// that joins the network later never heard the first one.
/// </para>
/// </summary>
public class MulticastDiscoveryClient : IPluginServiceDiscoveryClient, IAsyncDisposable
{
    private static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    private const int Port = 5353;
    private const uint AnnouncementTtl = 120;

    private static readonly TimeSpan Repeat = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _announcing = new();
    private readonly Lock _announcementLock = new();
    private readonly Func<byte[], CancellationToken, Task> _send;
    private readonly TimeSpan _repeat;

    public MulticastDiscoveryClient()
        : this(SendMulticastAsync, Repeat) { }

    internal MulticastDiscoveryClient(Func<byte[], CancellationToken, Task> send, TimeSpan repeat)
    {
        _send = send;
        _repeat = repeat;
    }

    public async IAsyncEnumerable<PluginDiscoveredService> BrowseAsync(
        string protocol,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        using UdpClient client = Listening();

        await client.SendAsync(
            MulticastDnsMessage.Query(protocol),
            new IPEndPoint(Group, Port),
            ct
        );

        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;

            try
            {
                received = await client.ReceiveAsync(ct);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (SocketException)
            {
                // One malformed or refused datagram is not a reason to stop
                // listening: discovery runs for as long as the plugin wants it.
                continue;
            }

            if (Answer(received.Buffer, protocol, received.RemoteEndPoint) is { } service)
                yield return service;
        }
    }

    public async Task AnnounceAsync(
        string protocol,
        string instance,
        int port,
        IReadOnlyDictionary<string, string>? attributes,
        CancellationToken ct = default
    )
    {
        CancellationTokenSource repeating = new();
        CancellationToken repeatToken = repeating.Token;
        string key = Key(protocol, instance);

        lock (_announcementLock)
        {
            if (_announcing.TryGetValue(key, out CancellationTokenSource? previous))
            {
                previous.Cancel();
                previous.Dispose();
            }

            _announcing[key] = repeating;
        }

        byte[] message = MulticastDnsMessage.Announcement(
            protocol,
            instance,
            port,
            attributes,
            Dns.GetHostName(),
            AnnouncementTtl
        );

        await _send(message, ct);

        _ = Task.Run(
            async () =>
            {
                try
                {
                    using PeriodicTimer timer = new(_repeat);

                    while (await timer.WaitForNextTickAsync(repeatToken))
                        await _send(message, repeatToken);
                }
                catch (OperationCanceledException)
                {
                    // Stopped, which is the normal way this ends.
                }
            },
            repeatToken
        );
    }

    /// <summary>
    /// A goodbye rather than silence: the same records with a lifetime of
    /// zero, so every listener drops the instance now instead of holding it
    /// for another two minutes.
    /// </summary>
    public async Task StopAsync(string protocol, string instance, CancellationToken ct = default)
    {
        lock (_announcementLock)
        {
            if (
                _announcing.TryRemove(
                    Key(protocol, instance),
                    out CancellationTokenSource? repeating
                )
            )
            {
                repeating.Cancel();
                repeating.Dispose();
            }
        }

        await _send(
            MulticastDnsMessage.Announcement(protocol, instance, 0, null, Dns.GetHostName(), 0),
            ct
        );
    }

    public ValueTask DisposeAsync()
    {
        lock (_announcementLock)
        {
            foreach (CancellationTokenSource repeating in _announcing.Values)
            {
                repeating.Cancel();
                repeating.Dispose();
            }

            _announcing.Clear();
        }

        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private static string Key(string protocol, string instance) => $"{protocol}/{instance}";

    private static UdpClient Listening()
    {
        UdpClient client = new();
        client.Client.SetSocketOption(
            SocketOptionLevel.Socket,
            SocketOptionName.ReuseAddress,
            true
        );
        client.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
        client.JoinMulticastGroup(Group);

        return client;
    }

    private static async Task SendMulticastAsync(byte[] message, CancellationToken ct)
    {
        using UdpClient client = new();
        await client.SendAsync(message, new IPEndPoint(Group, Port), ct);
    }

    /// <summary>
    /// One response read into a service, or null when it answers a different
    /// question. The sender's address is the fallback host: a response whose
    /// SRV target does not resolve is still a machine that just spoke.
    /// </summary>
    private static PluginDiscoveredService? Answer(byte[] message, string protocol, IPEndPoint from)
    {
        IReadOnlyList<MulticastDnsRecord> records = MulticastDnsMessage.Read(message);

        MulticastDnsRecord? pointer = records.FirstOrDefault(record =>
            record.Type == MulticastDnsMessage.TypePtr
            && record.Name.StartsWith(protocol, StringComparison.OrdinalIgnoreCase)
        );

        if (pointer is null)
            return null;

        string full = MulticastDnsMessage.ReadPointer(message, pointer);
        string instance = full.Split('.').FirstOrDefault() ?? full;

        MulticastDnsRecord? service = records.FirstOrDefault(record =>
            record.Type == MulticastDnsMessage.TypeSrv
        );
        MulticastDnsRecord? text = records.FirstOrDefault(record =>
            record.Type == MulticastDnsMessage.TypeTxt
        );

        (string host, int port) = service is null
            ? (string.Empty, 0)
            : MulticastDnsMessage.ReadService(message, service);

        return new(
            protocol,
            instance,
            string.IsNullOrEmpty(host) ? from.Address.ToString() : host,
            port,
            text is null ? new Dictionary<string, string>() : MulticastDnsMessage.ReadText(text)
        );
    }
}
