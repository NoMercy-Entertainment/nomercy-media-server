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

using NoMercy.Networking.Messaging;
using Xunit;

namespace NoMercy.Tests.Networking;

/// <summary>
/// REQUIREMENT: one device arriving is one event, however many hubs it opens.
///
/// Every hub in the app derives from ConnectionHub, so a single client opens five or six
/// connections within milliseconds of each other. The activity log recorded one row per
/// connection, which is how it ended up holding two thousand "connected" rows against fourteen
/// "playback started" — the log was almost entirely its own noise.
///
/// The counting has to be atomic. Scanning the connection map for "is this device already
/// here" reads clean for every one of those concurrent hubs, because none of them has been
/// written to the map yet.
/// </summary>
[Trait("Category", "Unit")]
public sealed class DevicePresenceTrackingTests
{
    private const string DeviceId = "01KXXNBS0D60";

    [Fact]
    public void First_connection_reports_the_device_as_arriving()
    {
        ConnectedClients clients = new();

        clients.RegisterDeviceConnection(DeviceId).Should().BeTrue();
    }

    [Fact]
    public void Further_connections_from_the_same_device_are_not_arrivals()
    {
        ConnectedClients clients = new();
        clients.RegisterDeviceConnection(DeviceId);

        clients.RegisterDeviceConnection(DeviceId).Should().BeFalse();
        clients.RegisterDeviceConnection(DeviceId).Should().BeFalse();
    }

    [Fact]
    public void Different_devices_each_arrive_once()
    {
        ConnectedClients clients = new();

        clients.RegisterDeviceConnection("device-a").Should().BeTrue();
        clients.RegisterDeviceConnection("device-b").Should().BeTrue();
    }

    [Fact]
    public void Device_has_not_left_while_other_connections_remain()
    {
        ConnectedClients clients = new();
        clients.RegisterDeviceConnection(DeviceId);
        clients.RegisterDeviceConnection(DeviceId);
        clients.RegisterDeviceConnection(DeviceId);

        clients.ReleaseDeviceConnection(DeviceId).Should().BeFalse();
        clients.ReleaseDeviceConnection(DeviceId).Should().BeFalse();
    }

    [Fact]
    public void Device_leaves_when_its_last_connection_closes()
    {
        ConnectedClients clients = new();
        clients.RegisterDeviceConnection(DeviceId);
        clients.RegisterDeviceConnection(DeviceId);

        clients.ReleaseDeviceConnection(DeviceId);

        clients.ReleaseDeviceConnection(DeviceId).Should().BeTrue();
    }

    [Fact]
    public void Reconnecting_after_leaving_arrives_again()
    {
        ConnectedClients clients = new();
        clients.RegisterDeviceConnection(DeviceId);
        clients.ReleaseDeviceConnection(DeviceId);

        clients.RegisterDeviceConnection(DeviceId).Should().BeTrue();
    }

    /// <summary>
    /// The one that matters: the hubs really do connect at the same time, and the bug this
    /// replaces was invisible to a sequential test — a scan-based check passes every
    /// assertion above and still writes six rows in production.
    /// </summary>
    [Fact]
    public void Concurrent_connections_from_one_device_produce_exactly_one_arrival()
    {
        ConnectedClients clients = new();
        const int hubCount = 32;

        bool[] results = new bool[hubCount];
        using Barrier gate = new(hubCount);

        Parallel.For(
            0,
            hubCount,
            index =>
            {
                // Line every thread up so they contend rather than trickle through.
                gate.SignalAndWait();
                results[index] = clients.RegisterDeviceConnection(DeviceId);
            }
        );

        results.Count(arrived => arrived).Should().Be(1);
    }

    [Fact]
    public void Concurrent_disconnects_produce_exactly_one_departure()
    {
        ConnectedClients clients = new();
        const int hubCount = 32;

        for (int i = 0; i < hubCount; i++)
            clients.RegisterDeviceConnection(DeviceId);

        bool[] results = new bool[hubCount];
        using Barrier gate = new(hubCount);

        Parallel.For(
            0,
            hubCount,
            index =>
            {
                gate.SignalAndWait();
                results[index] = clients.ReleaseDeviceConnection(DeviceId);
            }
        );

        results.Count(left => left).Should().Be(1);
    }

    [Fact]
    public void A_device_with_no_id_is_never_reported_as_arriving_or_leaving()
    {
        ConnectedClients clients = new();

        clients.RegisterDeviceConnection(string.Empty).Should().BeFalse();
        clients.ReleaseDeviceConnection(string.Empty).Should().BeFalse();
    }
}
