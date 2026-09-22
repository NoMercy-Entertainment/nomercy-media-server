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

using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginSdk.Network;

/// <summary>
/// NAT-PMP, RFC 6886: twelve bytes to the default gateway on UDP 5351.
/// <para>
/// Chosen over UPnP because it is small enough to implement correctly here and
/// is what most home routers answer. A router that does not answer is not an
/// error the plugin can fix, so the refusal says the router declined rather
/// than blaming the manifest.
/// </para>
/// </summary>
public class NatPortMapClient : IPluginPortMapClient
{
    private const int NatPmpPort = 5351;
    private const byte Version = 0;
    private const byte OpcodeMapUdp = 1;
    private const byte OpcodeMapTcp = 2;

    private static readonly TimeSpan Answer = TimeSpan.FromSeconds(3);

    public async Task<int> MapAsync(
        int internalPort,
        int externalPort,
        PluginTransport transport,
        TimeSpan lease,
        CancellationToken ct = default
    )
    {
        byte[] answer = await AskAsync(internalPort, externalPort, transport, lease, ct);

        // 0 is success. Anything else is the router saying no, which no change
        // to the plugin can fix.
        ushort result = BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(2, 2));
        if (result != 0)
            throw new PluginRefusedException(
                PluginRefusalMessages.RouterDeclined(externalPort, result)
            );

        return BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(10, 2));
    }

    /// <summary>
    /// A lease of zero is how NAT-PMP says "remove this", so unmapping is the
    /// same request with the clock set to nothing.
    /// </summary>
    public async Task UnmapAsync(PluginPortMapping mapping, CancellationToken ct = default)
    {
        await AskAsync(mapping.InternalPort, 0, mapping.Transport, TimeSpan.Zero, ct);
    }

    private static async Task<byte[]> AskAsync(
        int internalPort,
        int externalPort,
        PluginTransport transport,
        TimeSpan lease,
        CancellationToken ct
    )
    {
        IPAddress gateway =
            DefaultGateway()
            ?? throw new PluginRefusedException(PluginRefusalMessages.NoRouterFound());

        byte[] request = new byte[12];
        request[0] = Version;
        request[1] = transport == PluginTransport.Udp ? OpcodeMapUdp : OpcodeMapTcp;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4, 2), (ushort)internalPort);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(6, 2), (ushort)externalPort);
        BinaryPrimitives.WriteUInt32BigEndian(
            request.AsSpan(8, 4),
            (uint)Math.Max(0, lease.TotalSeconds)
        );

        using UdpClient client = new();
        client.Connect(new IPEndPoint(gateway, NatPmpPort));

        await client.SendAsync(request, ct);

        using CancellationTokenSource giveUp = CancellationTokenSource.CreateLinkedTokenSource(ct);
        giveUp.CancelAfter(Answer);

        try
        {
            UdpReceiveResult received = await client.ReceiveAsync(giveUp.Token);

            if (received.Buffer.Length < 12)
                throw new PluginRefusedException(PluginRefusalMessages.NoRouterFound());

            return received.Buffer;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Silence is the usual answer from a router with NAT-PMP switched
            // off, and it is not a cancellation the caller asked for.
            throw new PluginRefusedException(PluginRefusalMessages.NoRouterFound());
        }
    }

    /// <summary>
    /// The first gateway on an interface that is actually up. A machine with a
    /// VPN or a virtual switch has several, and the down ones list gateways
    /// that answer nothing.
    /// </summary>
    private static IPAddress? DefaultGateway() =>
        NetworkInterface
            .GetAllNetworkInterfaces()
            .Where(adapter =>
                adapter.OperationalStatus == OperationalStatus.Up
                && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback
            )
            .SelectMany(adapter => adapter.GetIPProperties().GatewayAddresses)
            .Select(gateway => gateway.Address)
            .FirstOrDefault(address =>
                address is not null && address.AddressFamily == AddressFamily.InterNetwork
            );
}
