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

using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using NoMercy.Api.Hubs;
using NoMercy.Api.WebSockets;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Users;
using NoMercy.Networking.Discovery;

namespace NoMercy.Tests.Repositories;

[Trait("Category", "Device")]
public class DeviceRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;
    private static readonly Guid OwnerId = Guid.Parse("aaaabbbb-cccc-dddd-eeee-111111111111");
    private static readonly Guid OtherOwnerId = Guid.Parse("ffffeeee-dddd-cccc-bbbb-222222222222");

    public DeviceRepositoryTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();

        // Foreign keys must be ON so the cascade behaviour configured in
        // MediaContext.OnModelCreating is actually enforced by SQLite.
        using (SqliteCommand fkOn = _connection.CreateCommand())
        {
            fkOn.CommandText = "PRAGMA foreign_keys = ON;";
            fkOn.ExecuteNonQuery();
        }

        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext ctx = new(_options);
        ctx.Database.EnsureCreated();

        // Seed the two owner User rows required by Device.OwnerUserId FK.
        ctx.Users.AddRange(
            new User
            {
                Id = OwnerId,
                Email = "owner@test.local",
                Name = "Owner",
            },
            new User
            {
                Id = OtherOwnerId,
                Email = "other@test.local",
                Name = "Other Owner",
            }
        );
        ctx.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private MediaContext OpenContext()
    {
        return new(_options);
    }

    private DeviceRepository BuildRepo(MediaContext ctx)
    {
        return new(ctx);
    }

    private static Device MakeDevice(Guid ownerUserId, string suffix = "A")
    {
        return new()
        {
            Id = Ulid.NewUlid(),
            DeviceId = $"device-{suffix}-{Guid.NewGuid()}",
            Name = $"Device {suffix}",
            Type = "phone",
            Browser = "xunit",
            Os = "TestOS",
            Version = "1.0",
            Ip = "127.0.0.1",
            OwnerUserId = ownerUserId,
        };
    }

    // =========================================================================
    // DeleteDeviceWithLogsAsync — device with ActivityLogs does not throw
    // =========================================================================

    [Fact]
    public async Task DeleteDeviceWithLogsAsync_DeviceHasActivityLogs_DeletesWithoutFkThrow()
    {
        Device device = MakeDevice(OwnerId);

        await using MediaContext seedCtx = OpenContext();
        seedCtx.Devices.Add(device);
        await seedCtx.SaveChangesAsync();

        ActivityLog log = new()
        {
            Category = ActivityCategory.Auth,
            Type = "auth.login",
            Time = DateTime.UtcNow,
            Success = true,
            UserId = OwnerId,
            DeviceId = device.Id,
        };
        seedCtx.ActivityLogs.Add(log);
        await seedCtx.SaveChangesAsync();

        await using MediaContext deleteCtx = OpenContext();
        DeviceRepository repo = BuildRepo(deleteCtx);

        Func<Task> act = () => repo.DeleteDeviceWithLogsAsync(device.Id);
        await act.Should().NotThrowAsync();

        await using MediaContext verifyCtx = OpenContext();
        bool deviceGone = !await verifyCtx.Devices.AnyAsync(d => d.Id == device.Id);
        bool logsGone = !await verifyCtx.ActivityLogs.AnyAsync(l => l.DeviceId == device.Id);

        deviceGone.Should().BeTrue("device row must be removed");
        logsGone.Should().BeTrue("activity log rows must be removed before the device");
    }

    // =========================================================================
    // GetOwnerDeviceAsync — returns null for another user's device (404 guard)
    // =========================================================================

    [Fact]
    public async Task GetOwnerDeviceAsync_WrongOwner_ReturnsNull()
    {
        Device device = MakeDevice(OwnerId);

        await using MediaContext seedCtx = OpenContext();
        seedCtx.Devices.Add(device);
        await seedCtx.SaveChangesAsync();

        await using MediaContext queryCtx = OpenContext();
        DeviceRepository repo = BuildRepo(queryCtx);

        Device? result = await repo.GetOwnerDeviceAsync(device.Id, OtherOwnerId);

        result.Should().BeNull("a different user must not see another owner's device");
    }

    [Fact]
    public async Task GetOwnerDeviceAsync_CorrectOwner_ReturnsDevice()
    {
        Device device = MakeDevice(OwnerId);

        await using MediaContext seedCtx = OpenContext();
        seedCtx.Devices.Add(device);
        await seedCtx.SaveChangesAsync();

        await using MediaContext queryCtx = OpenContext();
        DeviceRepository repo = BuildRepo(queryCtx);

        Device? result = await repo.GetOwnerDeviceAsync(device.Id, OwnerId);

        result.Should().NotBeNull();
        result!.Id.Should().Be(device.Id);
    }

    // =========================================================================
    // GetOwnerDevicesAsync — only returns devices for that owner
    // =========================================================================

    [Fact]
    public async Task GetOwnerDevicesAsync_ReturnsOnlyDevicesForOwner()
    {
        Device owned1 = MakeDevice(OwnerId, "X1");
        Device owned2 = MakeDevice(OwnerId, "X2");
        Device other = MakeDevice(OtherOwnerId, "Y1");

        await using MediaContext seedCtx = OpenContext();
        seedCtx.Devices.AddRange(owned1, owned2, other);
        await seedCtx.SaveChangesAsync();

        await using MediaContext queryCtx = OpenContext();
        DeviceRepository repo = BuildRepo(queryCtx);

        List<Device> result = await repo.GetOwnerDevicesAsync(OwnerId);

        result.Should().HaveCount(2);
        result.Should().AllSatisfy(d => d.OwnerUserId.Should().Be(OwnerId));
    }

    // =========================================================================
    // Offline-prune predicate: never removes an online device
    // =========================================================================

    [Fact]
    public async Task OfflinePrune_NeverDeletesOnlineDevice()
    {
        Device onlineDevice = MakeDevice(OwnerId, "ONLINE");
        Device offlineDevice = MakeDevice(OwnerId, "OFFLINE");

        await using MediaContext seedCtx = OpenContext();
        seedCtx.Devices.AddRange(onlineDevice, offlineDevice);
        await seedCtx.SaveChangesAsync();

        Mock<IDbContextFactory<MediaContext>> factoryMock = new();
        factoryMock
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(_options));

        Mock<IHubContext<DeviceHub>> hubMock = new();
        Mock<IHubClients> clientsMock = new();
        Mock<IClientProxy> proxyMock = new();
        clientsMock.Setup(c => c.User(It.IsAny<string>())).Returns(proxyMock.Object);
        hubMock.Setup(h => h.Clients).Returns(clientsMock.Object);

        DeviceBusRegistry registry = new(
            new DeviceStateRepository(factoryMock.Object),
            hubMock.Object,
            Mock.Of<ICastMdnsRegistry>()
        );

        await registry.Register(onlineDevice.Id, null!);

        await using MediaContext queryCtx = OpenContext();
        DeviceRepository repo = BuildRepo(queryCtx);
        List<Device> allOwned = await repo.GetOwnerDevicesAsync(OwnerId);

        List<Device> toDelete = allOwned.Where(d => !registry.IsOnline(d.Id)).ToList();

        toDelete.Should().ContainSingle(d => d.Id == offlineDevice.Id);
        toDelete.Should().NotContain(d => d.Id == onlineDevice.Id);
    }

    // =========================================================================
    // BroadcastChange fires after delete
    // =========================================================================

    [Fact]
    public async Task BroadcastChange_FiresAfterDeviceDelete()
    {
        Device device = MakeDevice(OwnerId);

        await using MediaContext seedCtx = OpenContext();
        seedCtx.Devices.Add(device);
        await seedCtx.SaveChangesAsync();

        Mock<IDbContextFactory<MediaContext>> factoryMock = new();
        factoryMock
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(_options));

        Mock<IHubContext<DeviceHub>> hubMock = new();
        Mock<IHubClients> clientsMock = new();
        Mock<IClientProxy> proxyMock = new();
        clientsMock.Setup(c => c.User(OwnerId.ToString())).Returns(proxyMock.Object);
        hubMock.Setup(h => h.Clients).Returns(clientsMock.Object);

        DeviceBusRegistry registry = new(
            new DeviceStateRepository(factoryMock.Object),
            hubMock.Object,
            Mock.Of<ICastMdnsRegistry>()
        );

        await using MediaContext deleteCtx = OpenContext();
        DeviceRepository repo = BuildRepo(deleteCtx);
        await repo.DeleteDeviceWithLogsAsync(device.Id);

        await registry.BroadcastChange(OwnerId);

        proxyMock.Verify(
            p =>
                p.SendCoreAsync(
                    "DeviceListChanged",
                    It.IsAny<object[]>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once,
            "BroadcastChange must send DeviceListChanged to the owner"
        );
    }

    // =========================================================================
    // GetOwnedDeviceAsync — by client device id, owner and type
    // =========================================================================

    [Fact]
    public async Task GetOwnedDeviceAsync_MatchesOnlyTheOwnersDeviceOfThatType()
    {
        Device tv = MakeDevice(OwnerId, "tv");
        tv.Type = "tv";
        Device othersTv = MakeDevice(OtherOwnerId, "other-tv");
        othersTv.Type = "tv";
        othersTv.DeviceId = tv.DeviceId + "-other";
        Device phone = MakeDevice(OwnerId, "phone");

        await using (MediaContext seedCtx = OpenContext())
        {
            seedCtx.Devices.AddRange(tv, othersTv, phone);
            await seedCtx.SaveChangesAsync();
        }

        await using MediaContext ctx = OpenContext();
        DeviceRepository repo = BuildRepo(ctx);

        (await repo.GetOwnedDeviceAsync(tv.DeviceId, OwnerId, "tv"))!.Id.Should().Be(tv.Id);
        (await repo.GetOwnedDeviceAsync(othersTv.DeviceId, OwnerId, "tv")).Should().BeNull();
        (await repo.GetOwnedDeviceAsync(phone.DeviceId, OwnerId, "tv")).Should().BeNull();
    }
}
