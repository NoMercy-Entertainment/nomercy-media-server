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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Users;
using Xunit;

namespace NoMercy.Tests.Repositories;

public class DeviceStateRepositoryTests : IDisposable
{
    private static readonly Guid OwnerId = Guid.Parse("aaaabbbb-cccc-dddd-eeee-333333333333");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;
    private readonly DeviceStateRepository _repository;

    public DeviceStateRepositoryTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using (MediaContext context = new(_options))
        {
            context.Database.EnsureCreated();
            context.Users.Add(
                new User
                {
                    Id = OwnerId,
                    Email = "owner@test.local",
                    Name = "Owner",
                }
            );
            context.SaveChanges();
        }

        Mock<IDbContextFactory<MediaContext>> factory = new();
        factory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(_options));
        _repository = new(factory.Object);
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Device Seed(string deviceId, string? fingerprint)
    {
        Device device = new()
        {
            Id = Ulid.NewUlid(),
            DeviceId = deviceId,
            Fingerprint = fingerprint,
            Name = "Living room TV",
            Type = "tv",
            Browser = "xunit",
            Os = "TestOS",
            Version = "1.0",
            Ip = "127.0.0.1",
            OwnerUserId = OwnerId,
        };
        using MediaContext context = new(_options);
        context.Devices.Add(device);
        context.SaveChanges();
        return device;
    }

    [Fact]
    public async Task GetOwnerAsync_ReturnsTheOwnerOrNullForAnUnknownDevice()
    {
        Device device = Seed("tv-1", "tv-1");

        (await _repository.GetOwnerAsync(device.Id)).Should().Be(OwnerId);
        (await _repository.GetOwnerAsync(Ulid.NewUlid())).Should().BeNull();
    }

    [Fact]
    public async Task GetListedDevicesAsync_LeavesOutRetiredRows()
    {
        Device listed = Seed("tv-1", "tv-1");
        Seed("tv-old", null);

        List<Device> devices = await _repository.GetListedDevicesAsync(OwnerId);

        devices.Should().ContainSingle().Which.Id.Should().Be(listed.Id);
    }

    [Fact]
    public async Task SetVolumeAsync_StoresTheVolumeOnTheDevice()
    {
        Device device = Seed("tv-1", "tv-1");

        await _repository.SetVolumeAsync("tv-1", 42);

        await using MediaContext context = new(_options);
        (await context.Devices.SingleAsync(d => d.Id == device.Id)).VolumePercent.Should().Be(42);
    }

    [Fact]
    public async Task RetireSupersededAsync_RetiresOnlyUnreachableRowsOfTheSameDevice()
    {
        Device current = Seed("tv-new", "tv-new");
        Device stale = Seed("tv-old", "tv-old");

        bool retired = await _repository.RetireSupersededAsync(current, OwnerId, _ => false);

        retired.Should().BeTrue();
        await using MediaContext context = new(_options);
        Device reloaded = await context.Devices.SingleAsync(d => d.Id == stale.Id);
        reloaded.Fingerprint.Should().BeNull();
        reloaded.IsActive.Should().BeFalse();
        (await context.Devices.SingleAsync(d => d.Id == current.Id))
            .Fingerprint.Should()
            .Be("tv-new");
    }
}
