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

    public void Dispose() => Service.Dispose();
}
