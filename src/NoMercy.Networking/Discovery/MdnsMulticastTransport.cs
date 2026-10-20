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
using Makaretu.Dns;

namespace NoMercy.Networking.Discovery;

/// <summary>
/// The production transport: one <see cref="MulticastService"/> that joins
/// the mDNS group on <see cref="Start"/> and leaves it on <see cref="Stop"/>.
/// </summary>
public sealed class MdnsMulticastTransport : IMdnsMulticastTransport
{
    private const int MdnsPort = 5353;
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(2);

    public MulticastService Service { get; } = new();

    public void Start() => Service.Start();

    public void Stop() => Service.Stop();

    public void SendUnicastResponseQuery(DomainName service) =>
        Service.SendUnicastQuery(service, DnsClass.IN, DnsType.PTR);

    public async Task<Message?> QueryHostAsync(
        IPAddress address,
        DomainName service,
        CancellationToken cancellationToken
    )
    {
        Message query = new() { Opcode = MessageOperation.Query };
        query.Questions.Add(
            new Question
            {
                Name = service,
                Class = DnsClass.IN,
                Type = DnsType.PTR,
            }
        );
        byte[] request = query.ToByteArray();

        using UdpClient client = new(address.AddressFamily);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        timeout.CancelAfter(ReplyTimeout);

        try
        {
            await client.SendAsync(request, new IPEndPoint(address, MdnsPort), timeout.Token);
            UdpReceiveResult reply = await client.ReceiveAsync(timeout.Token);

            Message answer = new();
            answer.Read(reply.Buffer, 0, reply.Buffer.Length);
            return answer;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<(Message Reply, string FromIp)>> QueryLegacyMulticastAsync(
        DomainName service,
        CancellationToken cancellationToken
    )
    {
        Message query = new() { Opcode = MessageOperation.Query };
        query.Questions.Add(
            new Question
            {
                Name = service,
                Class = DnsClass.IN,
                Type = DnsType.PTR,
            }
        );
        byte[] request = query.ToByteArray();
        IPEndPoint group = new(IPAddress.Parse("224.0.0.251"), MdnsPort);

        List<Task<List<(Message, string)>>> perInterface =
        [
            .. LanIPv4Addresses()
                .Select(local => AskFromAsync(local, request, group, cancellationToken)),
        ];
        List<(Message, string)>[] results = await Task.WhenAll(perInterface);
        return [.. results.SelectMany(r => r)];
    }

    private static async Task<List<(Message, string)>> AskFromAsync(
        IPAddress local,
        byte[] request,
        IPEndPoint group,
        CancellationToken cancellationToken
    )
    {
        List<(Message, string)> replies = [];
        using UdpClient client = new(new IPEndPoint(local, 0));
        client.Client.SetSocketOption(
            SocketOptionLevel.IP,
            SocketOptionName.MulticastInterface,
            local.GetAddressBytes()
        );
        using CancellationTokenSource window = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        window.CancelAfter(ReplyTimeout);

        try
        {
            await client.SendAsync(request, group, window.Token);
            while (true)
            {
                UdpReceiveResult reply = await client.ReceiveAsync(window.Token);
                Message answer = new();
                answer.Read(reply.Buffer, 0, reply.Buffer.Length);
                if (answer.IsResponse)
                    replies.Add((answer, reply.RemoteEndPoint.Address.ToString()));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return replies;
        }
        catch (SocketException)
        {
            return replies;
        }
    }

    private static IEnumerable<IPAddress> LanIPv4Addresses() =>
        System
            .Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n =>
                n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                && n.NetworkInterfaceType
                    != System.Net.NetworkInformation.NetworkInterfaceType.Loopback
            )
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork);

    public void Dispose() => Service.Dispose();
}
