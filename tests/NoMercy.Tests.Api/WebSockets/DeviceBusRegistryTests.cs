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
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using NoMercy.Api.Hubs;
using NoMercy.Api.WebSockets;
using NoMercy.Database;
using NoMercy.Database.Models.Users;
using NoMercy.Networking.Discovery;
using Xunit;

namespace NoMercy.Tests.Api.WebSockets;

/// <summary>
/// WsConnectedAt is not "is the device-bus socket open right now" — it is
/// DeviceDropRuleCronJob's and DeviceListComposer's only record of when a
/// device was last known alive at all, across every connection type
/// (ConnectionHub.OnConnectedAsync writes it from every hub, not just the
/// device-bus one). Unregister used to null it on an ordinary, expected
/// device-bus disconnect — wiping that history even while the device stayed
/// live over MusicHub. Confirmed live, real TV, 2026-09-09: "Tv in
/// woonkamer" was disowned as abandoned, repeatedly, hours apart, while
/// under continuous real MusicHub control the whole time.
/// </summary>
public sealed class DeviceBusRegistryTests : IDisposable
{
    private sealed class SingleConnectionContextFactory : IDbContextFactory<MediaContext>
    {
        private readonly SqliteConnection _connection;

        public SingleConnectionContextFactory()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
            using SqliteCommand pragma = _connection.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            pragma.ExecuteNonQuery();

            using MediaContext bootstrap = CreateDbContext();
            bootstrap.Database.EnsureCreated();
        }

        public MediaContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options);

        public Task<MediaContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(CreateDbContext());

        public void Dispose() => _connection.Dispose();
    }

    private readonly SingleConnectionContextFactory _contextFactory = new();
    private readonly Mock<IHubContext<DeviceHub>> _hubContext = new();
    private readonly Mock<ICastMdnsRegistry> _castMdnsRegistry = new();

    public DeviceBusRegistryTests()
    {
        Mock<IHubClients> clients = new();
        clients.Setup(c => c.User(It.IsAny<string>())).Returns(Mock.Of<IClientProxy>());
        _hubContext.Setup(h => h.Clients).Returns(clients.Object);
        _castMdnsRegistry.Setup(m => m.IsReachable(It.IsAny<string?>())).Returns(false);
    }

    public void Dispose() => _contextFactory.Dispose();

    private DeviceBusRegistry MakeRegistry() =>
        new(_contextFactory, _hubContext.Object, _castMdnsRegistry.Object);

    private async Task<Device> SeedOwnedDeviceAsync(DateTime wsConnectedAt)
    {
        Guid ownerId = Guid.NewGuid();
        Device device = new()
        {
            DeviceId = "device-drop-regression",
            Fingerprint = "fp-drop-regression",
            Name = "Tv in woonkamer",
            Type = "tv",
            OwnerUserId = ownerId,
            WsConnectedAt = wsConnectedAt,
        };
        await using MediaContext ctx = await _contextFactory.CreateDbContextAsync();
        ctx.Devices.Add(device);
        await ctx.SaveChangesAsync();
        return device;
    }

    [Fact]
    public async Task Unregister_LeavesWsConnectedAtUntouched()
    {
        DateTime seenViaMusicHub = DateTime.UtcNow;
        Device device = await SeedOwnedDeviceAsync(seenViaMusicHub);
        DeviceBusRegistry registry = MakeRegistry();

        // The device-bus socket for this device drops — an ordinary,
        // expected event that happens far more often than a genuine
        // multi-hour absence.
        await registry.Unregister(device.Id);

        await using MediaContext ctx = await _contextFactory.CreateDbContextAsync();
        Device reloaded = await ctx.Devices.SingleAsync(d => d.Id == device.Id);
        Assert.Equal(seenViaMusicHub, reloaded.WsConnectedAt);
    }

    [Fact]
    public async Task Unregister_RemovesFromLiveSet_EvenThoughWsConnectedAtSurvives()
    {
        Device device = await SeedOwnedDeviceAsync(DateTime.UtcNow);
        DeviceBusRegistry registry = MakeRegistry();
        await registry.Register(device.Id, Mock.Of<WebSocket>(ws => ws.State == WebSocketState.Open));
        Assert.True(registry.IsOnline(device.Id));

        await registry.Unregister(device.Id);

        Assert.False(
            registry.IsOnline(device.Id),
            "the live in-memory socket set is a separate concept from the DB's last-seen timestamp — this must still clear"
        );
    }
}
