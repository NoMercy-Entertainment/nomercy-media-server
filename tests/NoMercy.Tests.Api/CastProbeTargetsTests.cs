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
using NoMercy.Database;
using NoMercy.Database.Models.Users;
using NoMercy.Networking.Discovery;
using Xunit;

namespace NoMercy.Tests.Api;

// After a server restart the Cast scanner's memory is empty. A web-receiver TV has no
// LanIp (no _nomercy._tcp announcement), so the address it last connected from is the
// only stored way to ask it for its Cast announcement again.
[Trait("Category", "Unit")]
public class CastProbeTargetsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _dbOptions;

    public CastProbeTargetsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext seedContext = new(_dbOptions);
        seedContext.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private void Seed(params Device[] devices)
    {
        using MediaContext context = new(_dbOptions);
        context.Devices.AddRange(devices);
        context.SaveChanges();
    }

    private static Device Row(string deviceId, string ip, string? lanIp = null) =>
        new()
        {
            DeviceId = deviceId,
            Type = "tv",
            Name = deviceId,
            Ip = ip,
            LanIp = lanIp,
        };

    private async Task<IReadOnlyCollection<string>> Targets() =>
        await new RegisteredDeviceCastProbeTargets(
            new TestDbContextFactory(_dbOptions)
        ).GetLanIpsAsync(CancellationToken.None);

    [Fact]
    public async Task A_device_that_last_connected_from_this_network_is_probed_after_a_restart()
    {
        Seed(Row("web-receiver", "192.168.1.21"));

        Assert.Contains("192.168.1.21", await Targets());
    }

    [Fact]
    public async Task A_device_that_last_connected_through_the_tunnel_is_not_probed()
    {
        Seed(Row("remote-tv", "203.0.113.7"));

        Assert.Empty(await Targets());
    }

    [Fact]
    public async Task The_scanner_written_lan_address_is_still_probed_once()
    {
        Seed(
            Row("kmp-tv", "192.168.1.80", "192.168.1.80"),
            Row("other", "198.51.100.4", "192.168.1.90")
        );

        Assert.Equal(["192.168.1.80", "192.168.1.90"], (await Targets()).Order());
    }

    private sealed class TestDbContextFactory(DbContextOptions<MediaContext> options)
        : IDbContextFactory<MediaContext>
    {
        public MediaContext CreateDbContext() => new(options);
    }
}
