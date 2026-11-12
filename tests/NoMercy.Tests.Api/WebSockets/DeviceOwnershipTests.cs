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
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NoMercy.Api.WebSockets;
using NoMercy.Authorization;
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

        (Device? device, Guid? previousOwner) = await DeviceStateRepository.ResolveOwnedDeviceAsync(
            ctx,
            "fp-1",
            userA,
            "TV",
            "tv"
        );

        previousOwner.Should().BeNull();
        device!.OwnerUserId.Should().Be(userA);
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

        (Device? device, Guid? previousOwner) = await DeviceStateRepository.ResolveOwnedDeviceAsync(
            ctx,
            "fp-1",
            userA,
            "TV",
            "tv"
        );

        // ownership moved to the account the device is now logged into
        device!.OwnerUserId.Should().Be(userA);
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

        (Device? device, Guid? previousOwner) = await DeviceStateRepository.ResolveOwnedDeviceAsync(
            ctx,
            "fp-1",
            userA,
            "TV",
            "tv"
        );

        device!.OwnerUserId.Should().Be(userA);
        // previousOwner == caller, so HandleHello skips the previous-owner refresh
        previousOwner.Should().Be(userA);
    }

    [Fact]
    public async Task ForgottenDevice_CannotRegisterAgainWhileStillSignedIn()
    {
        await using MediaContext ctx = MakeContext();
        Guid owner = Guid.NewGuid();
        Device device = new()
        {
            DeviceId = "forgotten-tv",
            Fingerprint = "forgotten-tv",
            OwnerUserId = owner,
            Name = "Guest TV",
            Type = "tv",
        };
        ctx.Devices.Add(device);
        await ctx.SaveChangesAsync();

        DeviceRepository repository = new(ctx);
        await repository.RevokeDeviceAsync(device);
        await repository.DeleteDeviceAsync(device);

        (Device? resolved, _) = await DeviceStateRepository.ResolveOwnedDeviceAsync(
            ctx,
            "forgotten-tv",
            owner,
            "Guest TV",
            "tv"
        );

        resolved.Should().BeNull();
        (await ctx.Devices.CountAsync(d => d.DeviceId == "forgotten-tv")).Should().Be(0);
    }

    [Fact]
    public async Task Forget_writesOneRevocationPerOwnerAndDevice()
    {
        await using MediaContext ctx = MakeContext();
        Guid owner = Guid.NewGuid();
        Device device = new()
        {
            DeviceId = "forgotten-tv",
            OwnerUserId = owner,
            Type = "tv",
        };
        DeviceRepository repository = new(ctx);

        await repository.RevokeDeviceAsync(device);
        await repository.RevokeDeviceAsync(device);

        RevokedDevice row = await ctx.RevokedDevices.SingleAsync();
        row.DeviceId.Should().Be("forgotten-tv");
        row.OwnerUserId.Should().Be(owner);
    }

    [Fact]
    public async Task CloseRevoked_sendsRevokedFrameThenCloses4403()
    {
        RecordingWebSocket ws = new();

        await DeviceBusRegistry.CloseRevokedAsync(ws);

        ws.Sent.Should().Be("{\"type\":\"revoked\"}");
        ws.ClosedWith.Should().Be((WebSocketCloseStatus)4403);
        ws.ClosedReason.Should().Be("device_forgotten");
    }

    [Fact]
    public async Task RevokedDevice_hubCheck_refusesAndCreatesNoRow()
    {
        await using MediaContext ctx = MakeContext();
        Guid owner = Guid.NewGuid();
        ctx.RevokedDevices.Add(
            new()
            {
                DeviceId = "forgotten-tv",
                OwnerUserId = owner,
                RevokedAt = DateTime.UtcNow,
            }
        );
        await ctx.SaveChangesAsync();

        (await ctx.IsRevokedAsync(owner, "forgotten-tv", null)).Should().BeTrue();
        (await ctx.Devices.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task OldClientHello_tokenOlderThanRevocation_isRefusedAndCreatesNoRow()
    {
        await using MediaContext ctx = MakeContext();
        Guid owner = Guid.NewGuid();
        DateTime revokedAt = DateTime.UtcNow;
        ctx.RevokedDevices.Add(
            new()
            {
                DeviceId = "forgotten-tv",
                OwnerUserId = owner,
                RevokedAt = revokedAt,
            }
        );
        await ctx.SaveChangesAsync();

        (Device? withOldToken, _) = await DeviceStateRepository.ResolveOwnedDeviceAsync(
            ctx,
            "forgotten-tv",
            owner,
            "Guest TV",
            "tv",
            revokedAt.AddMinutes(-5)
        );
        (Device? withoutClaim, _) = await DeviceStateRepository.ResolveOwnedDeviceAsync(
            ctx,
            "forgotten-tv",
            owner,
            "Guest TV",
            "tv"
        );

        withOldToken.Should().BeNull();
        withoutClaim.Should().BeNull();
        (await ctx.Devices.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Hello_withSignInAfterRevocation_clearsItAndRegistersTheDevice()
    {
        await using MediaContext ctx = MakeContext();
        Guid owner = Guid.NewGuid();
        DateTime revokedAt = DateTime.UtcNow;
        ctx.RevokedDevices.Add(
            new()
            {
                DeviceId = "forgotten-tv",
                OwnerUserId = owner,
                RevokedAt = revokedAt,
            }
        );
        await ctx.SaveChangesAsync();

        (Device? device, _) = await DeviceStateRepository.ResolveOwnedDeviceAsync(
            ctx,
            "forgotten-tv",
            owner,
            "Guest TV",
            "tv",
            revokedAt.AddMinutes(5)
        );

        device.Should().NotBeNull();
        (await ctx.RevokedDevices.CountAsync()).Should().Be(0);
        (await ctx.Devices.CountAsync(d => d.DeviceId == "forgotten-tv")).Should().Be(1);
    }

    private static async Task SeedRevocationAsync(MediaContext ctx, Guid owner, string? sid)
    {
        ctx.RevokedDevices.Add(
            new()
            {
                DeviceId = "forgotten-tv",
                OwnerUserId = owner,
                RevokedAt = DateTime.UtcNow,
                SessionId = sid,
            }
        );
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Hello_sameSid_isRefusedAndCreatesNoRow()
    {
        await using MediaContext ctx = MakeContext();
        Guid owner = Guid.NewGuid();
        await SeedRevocationAsync(ctx, owner, "sid-1");

        (Device? device, _) = await DeviceStateRepository.ResolveOwnedDeviceAsync(
            ctx,
            "forgotten-tv",
            owner,
            "Guest TV",
            "tv",
            DateTime.UtcNow.AddHours(1),
            "sid-1"
        );

        device.Should().BeNull();
        (await ctx.Devices.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Hello_newSid_clearsRevocationAndRegisters()
    {
        await using MediaContext ctx = MakeContext();
        Guid owner = Guid.NewGuid();
        await SeedRevocationAsync(ctx, owner, "sid-1");

        (Device? device, _) = await DeviceStateRepository.ResolveOwnedDeviceAsync(
            ctx,
            "forgotten-tv",
            owner,
            "Guest TV",
            "tv",
            null,
            "sid-2"
        );

        device.Should().NotBeNull();
        (await ctx.RevokedDevices.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Hello_noSidNoAuthTime_isRefused()
    {
        await using MediaContext ctx = MakeContext();
        Guid owner = Guid.NewGuid();
        await SeedRevocationAsync(ctx, owner, "sid-1");

        (Device? device, _) = await DeviceStateRepository.ResolveOwnedDeviceAsync(
            ctx,
            "forgotten-tv",
            owner,
            "Guest TV",
            "tv"
        );

        device.Should().BeNull();
        (await ctx.RevokedDevices.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Revocation_isPerOwner_anotherAccountCanStillPair()
    {
        await using MediaContext ctx = MakeContext();
        Guid forgetter = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        ctx.RevokedDevices.Add(new() { DeviceId = "forgotten-tv", OwnerUserId = forgetter });
        await ctx.SaveChangesAsync();

        (Device? device, _) = await DeviceStateRepository.ResolveOwnedDeviceAsync(
            ctx,
            "forgotten-tv",
            other,
            "Guest TV",
            "tv"
        );

        device.Should().NotBeNull();
        device!.OwnerUserId.Should().Be(other);
    }

    [Fact]
    public void Sid_readsMappedClaimTypeSid()
    {
        ClaimsPrincipal mapped = new(new ClaimsIdentity([new(ClaimTypes.Sid, "sid-1")]));
        ClaimsPrincipal raw = new(new ClaimsIdentity([new("sid", "sid-2")]));

        mapped.Sid().Should().Be("sid-1");
        raw.Sid().Should().Be("sid-2");
    }

    [Fact]
    public void AuthTime_readsTheClaimAsUtc_andIsNullWhenMissing()
    {
        ClaimsPrincipal withClaim = new(new ClaimsIdentity([new("auth_time", "1700000000")]));
        ClaimsPrincipal without = new(new ClaimsIdentity());

        withClaim.AuthTime().Should().Be(new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc));
        without.AuthTime().Should().BeNull();
    }

    private sealed class RecordingWebSocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;

        public string Sent { get; private set; } = string.Empty;
        public WebSocketCloseStatus? ClosedWith { get; private set; }
        public string? ClosedReason { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => ClosedWith;
        public override string? CloseStatusDescription => ClosedReason;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken
        )
        {
            ClosedWith = closeStatus;
            ClosedReason = statusDescription;
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken
        ) => CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override void Dispose() { }

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken
        )
        {
            Sent += Encoding.UTF8.GetString(buffer);
            return Task.CompletedTask;
        }
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
