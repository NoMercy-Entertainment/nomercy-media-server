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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NoMercy.Api.WebSockets;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Users;
using NoMercy.Networking.Http;
using NoMercy.Networking.Messaging;
using Xunit;

namespace NoMercy.Tests.Api.WebSockets;

/// <summary>
/// Cross-account isolation for the device bus: a device's ownership follows the
/// account it is authenticated as, so it can never stay attached to (or surface
/// on) the account that paired it first once it logs into another account.
/// </summary>
public class DeviceOwnershipTests
{
    private static MediaContext MakeContext()
    {
        // SQLite in-memory without "Foreign Keys=True" so we can seed devices with
        // arbitrary owner ids without materializing full User rows.
        SqliteConnection connection = new("DataSource=:memory:");
        connection.Open();
        // Devices reference Users via a FK; these unit tests exercise ownership
        // routing in isolation, so disable FK enforcement instead of materializing
        // full User rows.
        using (SqliteCommand pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            pragma.ExecuteNonQuery();
        }
        DbContextOptions<MediaContext> options = new DbContextOptionsBuilder<MediaContext>()
            .UseSqlite(connection)
            .Options;
        MediaContext context = new(options);
        context.Database.EnsureCreated();
        return context;
    }

    [Fact]
    public async Task NewDevice_isOwnedByConnectingUser()
    {
        await using MediaContext ctx = MakeContext();
        Guid userA = Guid.NewGuid();

        (Device device, Guid? previousOwner) = await DeviceStateRepository.ResolveOwnedDeviceAsync(
            ctx,
            "fp-1",
            userA,
            "TV",
            "tv"
        );

        previousOwner.Should().BeNull();
        device.OwnerUserId.Should().Be(userA);
    }

    [Fact]
    public async Task Device_transfersToNewAccount_andReportsPreviousOwner()
    {
        await using MediaContext ctx = MakeContext();
        Guid userA = Guid.NewGuid();
        Guid userB = Guid.NewGuid();
        ctx.Devices.Add(
            new()
            {
                DeviceId = "fp-1",
                Fingerprint = "fp-1",
                OwnerUserId = userB,
                Name = "TV",
                Type = "tv",
            }
        );
        await ctx.SaveChangesAsync();

        (Device device, Guid? previousOwner) = await DeviceStateRepository.ResolveOwnedDeviceAsync(
            ctx,
            "fp-1",
            userA,
            "TV",
            "tv"
        );

        // ownership moved to the account the device is now logged into
        device.OwnerUserId.Should().Be(userA);
        previousOwner.Should().Be(userB);
        // the existing row was re-owned, not duplicated (DeviceId is unique)
        (await ctx.Devices.CountAsync(d => d.DeviceId == "fp-1"))
            .Should()
            .Be(1);
    }

    [Fact]
    public async Task Reconnect_sameOwner_isNotATransfer()
    {
        await using MediaContext ctx = MakeContext();
        Guid userA = Guid.NewGuid();
        ctx.Devices.Add(
            new()
            {
                DeviceId = "fp-1",
                Fingerprint = "fp-1",
                OwnerUserId = userA,
                Name = "TV",
                Type = "tv",
            }
        );
        await ctx.SaveChangesAsync();

        (Device device, Guid? previousOwner) = await DeviceStateRepository.ResolveOwnedDeviceAsync(
            ctx,
            "fp-1",
            userA,
            "TV",
            "tv"
        );

        device.OwnerUserId.Should().Be(userA);
        // previousOwner == caller, so HandleHello skips the previous-owner refresh
        previousOwner.Should().Be(userA);
    }
}

/// <summary>
/// Regression coverage for the live incident: opening the app on a second device
/// paused whatever was actively playing on a TV that never budged. Root cause —
/// device-bus (a secondary, TV-only wake/status WebSocket, independent of MusicHub)
/// treated its OWN disconnect as proof the active device was gone and unconditionally
/// cleared the session's PlayState, even when that device was still fully connected
/// (and still playing) on MusicHub the whole time. A device-bus blip is common —
/// 30s ping cadence, OS-throttled background sockets, LAN churn from another device
/// coming online — and must never be able to kill live playback on its own.
/// <see cref="DeviceBusEndpoint.IsStillOnMusicHub"/> is the guard: only when
/// MusicHub agrees the device is gone too does the disconnect handler release the
/// active claim.
/// </summary>
public class DeviceBusMusicHubGuardTests
{
    private static Client MakeMusicHubClient(string deviceId)
    {
        return new()
        {
            Id = Ulid.NewUlid(),
            Sub = Guid.NewGuid(),
            DeviceId = deviceId,
            Endpoint = "/musicHub",
        };
    }

    [Fact]
    public void IsStillOnMusicHub_DeviceHasLiveMusicHubConnection_ReturnsTrue()
    {
        ConnectedClients connectedClients = new();
        string deviceId = $"tv-{Guid.NewGuid()}";
        connectedClients.Clients["conn-1"] = MakeMusicHubClient(deviceId);

        DeviceBusEndpoint.IsStillOnMusicHub(connectedClients, deviceId).Should().BeTrue();
    }

    [Fact]
    public void IsStillOnMusicHub_DeviceHasNoConnectionsAtAll_ReturnsFalse()
    {
        ConnectedClients connectedClients = new();
        string deviceId = $"tv-{Guid.NewGuid()}";

        DeviceBusEndpoint.IsStillOnMusicHub(connectedClients, deviceId).Should().BeFalse();
    }

    [Fact]
    public void IsStillOnMusicHub_OnlyOtherDevicesConnected_ReturnsFalse()
    {
        ConnectedClients connectedClients = new();
        string deviceId = $"tv-{Guid.NewGuid()}";
        connectedClients.Clients["conn-1"] = MakeMusicHubClient($"phone-{Guid.NewGuid()}");

        DeviceBusEndpoint.IsStillOnMusicHub(connectedClients, deviceId).Should().BeFalse();
    }

    [Fact]
    public void IsStillOnMusicHub_SameDeviceOnlyOnANonMusicHubEndpoint_ReturnsFalse()
    {
        // A device can hold connections to other hubs (deviceHub, dashboardHub,
        // castHub) without being on musicHub at all — those must not count.
        ConnectedClients connectedClients = new();
        string deviceId = $"tv-{Guid.NewGuid()}";
        connectedClients.Clients["conn-1"] = new()
        {
            Id = Ulid.NewUlid(),
            Sub = Guid.NewGuid(),
            DeviceId = deviceId,
            Endpoint = "/deviceHub",
        };

        DeviceBusEndpoint.IsStillOnMusicHub(connectedClients, deviceId).Should().BeFalse();
    }

    [Fact]
    public void IsStillOnMusicHub_DeviceIdComparisonIsCaseInsensitive()
    {
        ConnectedClients connectedClients = new();
        string deviceId = $"TV-{Guid.NewGuid()}";
        connectedClients.Clients["conn-1"] = MakeMusicHubClient(deviceId.ToLowerInvariant());

        DeviceBusEndpoint.IsStillOnMusicHub(connectedClients, deviceId).Should().BeTrue();
    }
}
