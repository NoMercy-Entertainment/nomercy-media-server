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
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Database;
using NoMercy.Database.Models.Queue;
using NoMercy.Queue.MediaServer.Jobs;
using NoMercyQueue.Core.Interfaces;
using Xunit;

namespace NoMercy.Tests.Queue;

public class DatabaseMaintenanceCronJobTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        Guid.NewGuid().ToString()
    );
    private readonly List<SqliteConnection> _connections = [];
    private readonly Dictionary<string, string> _settingValues = [];
    private readonly MaintenanceGate _gate = new();

    public DatabaseMaintenanceCronJobTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        foreach (SqliteConnection connection in _connections)
            connection.Dispose();

        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }

    private DatabaseMaintenanceSettings CreateSettings()
    {
        Mock<IConfigurationStore> store = new();
        store
            .Setup(x => x.GetValue(It.IsAny<string>()))
            .Returns((string key) => _settingValues.GetValueOrDefault(key));
        return new(store.Object);
    }

    private IDbContextFactory<MediaContext> CreateMediaFactory()
    {
        SqliteConnection connection = new("DataSource=:memory:;Foreign Keys=False");
        connection.Open();
        _connections.Add(connection);

        DbContextOptions<MediaContext> options = new DbContextOptionsBuilder<MediaContext>()
            .UseSqlite(connection)
            .Options;
        using (MediaContext init = new(options))
            init.Database.EnsureCreated();

        Mock<IDbContextFactory<MediaContext>> factory = new();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(options));
        return factory.Object;
    }

    private IDbContextFactory<QueueContext> CreateQueueFactory(string fileName)
    {
        DbContextOptions<QueueContext> options = new DbContextOptionsBuilder<QueueContext>()
            .UseSqlite($"Data Source={Path.Combine(_directory, fileName)};Pooling=False")
            .Options;
        using (QueueContext init = new(options))
        {
            init.Database.EnsureCreated();
            init.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        }

        Mock<IDbContextFactory<QueueContext>> factory = new();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(options));
        return factory.Object;
    }

    private DatabaseMaintenanceCronJob CreateJob(
        IDbContextFactory<MediaContext> mediaFactory,
        IDbContextFactory<QueueContext> queueFactory
    )
    {
        return new(
            mediaFactory,
            queueFactory,
            CreateSettings(),
            new SqliteCompactor(),
            _gate,
            NullLogger<DatabaseMaintenanceCronJob>.Instance
        );
    }

    private static async Task SeedFailedJobAsync(
        IDbContextFactory<QueueContext> factory,
        int ageDays,
        int payloadLength = 10
    )
    {
        await using QueueContext context = await factory.CreateDbContextAsync();
        context.FailedJobs.Add(
            new()
            {
                Uuid = Guid.NewGuid(),
                Queue = "encoder",
                Payload = new string('p', payloadLength),
                Exception = new string('e', payloadLength),
                FailedAt = DateTime.UtcNow.AddDays(-ageDays),
            }
        );
        await context.SaveChangesAsync();
    }

    private static async Task<List<int>> FailedAgesAsync(IDbContextFactory<QueueContext> factory)
    {
        await using QueueContext context = await factory.CreateDbContextAsync();
        List<DateTime> failedAt = await context
            .FailedJobs.AsNoTracking()
            .Select(f => f.FailedAt)
            .ToListAsync();
        return failedAt
            .Select(t => (int)Math.Round((DateTime.UtcNow - t).TotalDays))
            .OrderBy(age => age)
            .ToList();
    }

    [Fact]
    public async Task ExecuteAsync_FailedJobsOlderThanDefaultRetention_RemovesOnlyThose()
    {
        IDbContextFactory<QueueContext> queue = CreateQueueFactory("age.db");
        await SeedFailedJobAsync(queue, 45);
        await SeedFailedJobAsync(queue, 31);
        await SeedFailedJobAsync(queue, 2);

        await CreateJob(CreateMediaFactory(), queue).ExecuteAsync(string.Empty);

        (await FailedAgesAsync(queue)).Should().Equal(2);
    }

    [Fact]
    public async Task ExecuteAsync_RetentionSettingChanged_UsesTheConfiguredAge()
    {
        _settingValues[DatabaseMaintenanceSettings.FailedJobRetentionDaysKey] = "5";
        IDbContextFactory<QueueContext> queue = CreateQueueFactory("setting.db");
        await SeedFailedJobAsync(queue, 10);
        await SeedFailedJobAsync(queue, 4);

        await CreateJob(CreateMediaFactory(), queue).ExecuteAsync(string.Empty);

        (await FailedAgesAsync(queue)).Should().Equal(4);
    }

    [Fact]
    public async Task ExecuteAsync_RetentionSettingInvalid_FallsBackToDefault()
    {
        _settingValues[DatabaseMaintenanceSettings.FailedJobRetentionDaysKey] = "soon";
        IDbContextFactory<QueueContext> queue = CreateQueueFactory("invalid.db");
        await SeedFailedJobAsync(queue, 45);
        await SeedFailedJobAsync(queue, 20);

        await CreateJob(CreateMediaFactory(), queue).ExecuteAsync(string.Empty);

        (await FailedAgesAsync(queue)).Should().Equal(20);
    }

    [Fact]
    public async Task ExecuteAsync_PruneLeavesFreePages_ShrinksTheQueueDatabaseFile()
    {
        _settingValues[DatabaseMaintenanceSettings.MinFreeMegabytesKey] = "0";
        IDbContextFactory<QueueContext> queue = CreateQueueFactory("shrink.db");
        for (int i = 0; i < 300; i++)
            await SeedFailedJobAsync(queue, 60, 3000);
        await SeedFailedJobAsync(queue, 1);
        long sizeBefore = new FileInfo(Path.Combine(_directory, "shrink.db")).Length;

        await CreateJob(CreateMediaFactory(), queue).ExecuteAsync(string.Empty);

        (await FailedAgesAsync(queue)).Should().Equal(1);
        new FileInfo(Path.Combine(_directory, "shrink.db"))
            .Length.Should()
            .BeLessThan(sizeBefore / 2);
    }

    [Fact]
    public async Task RunAsync_PruneLeavesFreePages_ReportsTheRowsAndBytesFreed()
    {
        _settingValues[DatabaseMaintenanceSettings.MinFreeMegabytesKey] = "0";
        IDbContextFactory<QueueContext> queue = CreateQueueFactory("report.db");
        for (int i = 0; i < 300; i++)
            await SeedFailedJobAsync(queue, 60, 3000);
        await SeedFailedJobAsync(queue, 1);

        DatabaseMaintenanceResult result = await CreateJob(CreateMediaFactory(), queue)
            .RunAsync(CancellationToken.None);

        result.FailedJobsRemoved.Should().Be(300);
        result.Queue.Should().NotBeNull();
        result.Queue!.Compacted.Should().BeTrue();
        (result.Queue.BytesBefore - result.Queue.BytesAfter)
            .Should()
            .BeGreaterThan(result.Queue.BytesBefore / 2);
        new FileInfo(Path.Combine(_directory, "report.db"))
            .Length.Should()
            .Be(result.Queue.BytesAfter);
    }

    [Fact]
    public async Task ExecuteAsync_WhileACleanUpHoldsTheGate_WaitsAndPrunesAfterItLeaves()
    {
        IDbContextFactory<QueueContext> queue = CreateQueueFactory("gate.db");
        await SeedFailedJobAsync(queue, 45);
        IDisposable cleanUp = _gate.TryEnter()!;

        Task nightly = CreateJob(CreateMediaFactory(), queue).ExecuteAsync(string.Empty);
        await Task.Delay(200);
        nightly.IsCompleted.Should().BeFalse();
        (await FailedAgesAsync(queue)).Should().Equal(45);

        cleanUp.Dispose();
        await nightly.WaitAsync(TimeSpan.FromSeconds(10));

        (await FailedAgesAsync(queue)).Should().BeEmpty();
    }

    [Fact]
    public void CronExpression_NoSetting_RunsDailyAtHalfPastThree()
    {
        DatabaseMaintenanceCronJob job = CreateJob(
            CreateMediaFactory(),
            CreateQueueFactory("c1.db")
        );

        job.CronExpression.Should().Be("30 3 * * *");
    }

    [Fact]
    public void CronExpression_HourSettingChanged_UsesTheConfiguredHour()
    {
        _settingValues[DatabaseMaintenanceSettings.HourKey] = "5";
        DatabaseMaintenanceCronJob job = CreateJob(
            CreateMediaFactory(),
            CreateQueueFactory("c2.db")
        );

        job.CronExpression.Should().Be("30 5 * * *");
    }

    [Fact]
    public void CronExpression_HourSettingOutOfRange_FallsBackToDefault()
    {
        _settingValues[DatabaseMaintenanceSettings.HourKey] = "25";
        DatabaseMaintenanceCronJob job = CreateJob(
            CreateMediaFactory(),
            CreateQueueFactory("c3.db")
        );

        job.CronExpression.Should().Be("30 3 * * *");
    }
}
