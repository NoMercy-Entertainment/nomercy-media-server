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
using NoMercy.Database;
using NoMercy.Database.Models.Queue;
using NoMercy.Queue.MediaServer.Repositories;
using NoMercyQueue.Core.Models;
using Xunit;

namespace NoMercy.Tests.Queue.Repositories;

/// <summary>
/// Covers <see cref="QueueTaskRepository"/> against a real, in-memory SQLite
/// <see cref="QueueContext"/> — the raw-SQL paths (json_extract, case-when
/// counts) only prove out against a real SQLite engine, never a fake.
/// </summary>
public class QueueTaskRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestQueueContextFactory _factory;
    private readonly QueueTaskRepository _repository;

    public QueueTaskRepositoryTests()
    {
        _connection = new($"DataSource={Guid.NewGuid()};Mode=Memory;Cache=Shared");
        _connection.Open();

        DbContextOptions<QueueContext> options = new DbContextOptionsBuilder<QueueContext>()
            .UseSqlite(_connection)
            .Options;

        using (QueueContext init = new(options))
            init.Database.EnsureCreated();

        _factory = new(options);
        _repository = new(_factory);
    }

    public void Dispose() => _connection.Dispose();

    private QueueJob AddJob(
        string queue,
        int priority,
        string payload,
        DateTime? reservedAt = null,
        DateTime? createdAt = null
    )
    {
        using QueueContext context = _factory.CreateDbContext();
        QueueJob job = new()
        {
            Queue = queue,
            Priority = priority,
            Payload = payload,
            ReservedAt = reservedAt,
            AvailableAt = DateTime.UtcNow,
            CreatedAt = createdAt ?? DateTime.UtcNow,
        };
        context.QueueJobs.Add(job);
        context.SaveChanges();
        return job;
    }

    [Fact]
    public async Task GetRecentJobsAsync_OrdersByPriorityDescendingThenId_AndRespectsLimit()
    {
        AddJob("encoder", 1, "{}");
        AddJob("encoder", 5, "{}");
        AddJob("encoder", 3, "{}");

        List<QueueJobModel> jobs = await _repository.GetRecentJobsAsync(2);

        jobs.Should().HaveCount(2);
        jobs[0].Priority.Should().Be(5);
        jobs[1].Priority.Should().Be(3);
    }

    [Fact]
    public async Task GetEncoderPrioritiesAsync_ScopesEncoderCpuToMusicPayloadsOnly()
    {
        AddJob("encoder", 4, "{\"$type\":\"VideoEncodeJob\"}");
        AddJob("encoder-cpu", 5, "{\"$type\":\"MusicEncodeJob\"}");
        AddJob("encoder-cpu", 9, "{\"$type\":\"SomethingElse\"}");
        AddJob("import", 10, "{}");

        List<int> priorities = await _repository.GetEncoderPrioritiesAsync();

        priorities.Should().Equal(5, 4);
    }

    [Fact]
    public async Task GetEncoderJobsByPriorityAsync_ReturnsOnlyThatPriority_UpToLimit()
    {
        AddJob("encoder", 4, "{}");
        AddJob("encoder", 4, "{}");
        AddJob("encoder", 4, "{}");
        AddJob("encoder", 1, "{}");

        List<QueueJobModel> jobs = await _repository.GetEncoderJobsByPriorityAsync(4, 2);

        jobs.Should().HaveCount(2);
        jobs.Should().OnlyContain(job => job.Priority == 4);
    }

    [Fact]
    public async Task GetRunningEncoderJobsAsync_OnlyReturnsReservedRows()
    {
        AddJob("encoder", 4, "{}", DateTime.UtcNow);
        AddJob("encoder", 4, "{}");

        List<QueueJobModel> running = await _repository.GetRunningEncoderJobsAsync(10);

        running.Should().ContainSingle();
        running[0].ReservedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task CountQueuedTracksByReleaseAsync_GroupsByReleaseIdFromThePayload()
    {
        Guid releaseId = Guid.NewGuid();
        AddJob("encoder-cpu", 5, $$"""{"$type":"MusicEncodeJob","releaseId":"{{releaseId}}"}""");
        AddJob("encoder-cpu", 5, $$"""{"$type":"MusicEncodeJob","releaseId":"{{releaseId}}"}""");

        Dictionary<Guid, int> counts = await _repository.CountQueuedTracksByReleaseAsync();

        counts.Should().ContainKey(releaseId).WhoseValue.Should().Be(2);
    }

    [Fact]
    public async Task CountQueuedMusicAnalysisAsync_OnlyCountsAnalysisPayloadsOnTheMusicQueue()
    {
        AddJob("music", 1, "{\"$type\":\"MusicAnalysisJob\"}");
        AddJob("music", 1, "{\"$type\":\"MusicAnalysisJob\"}");
        AddJob("music", 1, "{\"$type\":\"SomethingElse\"}");

        (await _repository.CountQueuedMusicAnalysisAsync()).Should().Be(2);
    }

    [Fact]
    public async Task QueueExistsAsync_TrueOnlyWhenARowSitsOnThatQueue()
    {
        AddJob("encoder", 1, "{}");

        (await _repository.QueueExistsAsync("encoder")).Should().BeTrue();
        (await _repository.QueueExistsAsync("missing")).Should().BeFalse();
    }

    [Fact]
    public async Task GetJobsForQueueAsync_OrdersByPriorityDescendingThenCreatedAtThenId()
    {
        AddJob("import", 1, "{}", createdAt: DateTime.UtcNow.AddMinutes(-1));
        AddJob("import", 5, "{}");

        List<QueueJobModel> jobs = await _repository.GetJobsForQueueAsync("import");

        jobs.Should().HaveCount(2);
        jobs[0].Priority.Should().Be(5);
    }

    [Fact]
    public async Task SetPrioritiesAsync_PersistsANewPriorityPerJobId()
    {
        QueueJob job = AddJob("import", 1, "{}");

        await _repository.SetPrioritiesAsync(new Dictionary<int, int> { [job.Id] = 42 });

        using QueueContext context = _factory.CreateDbContext();
        (await context.QueueJobs.FindAsync(job.Id))!.Priority.Should().Be(42);
    }

    [Fact]
    public async Task GetFailedJobsAsync_OrdersByFailedAtDescending()
    {
        using (QueueContext context = _factory.CreateDbContext())
        {
            context.FailedJobs.AddRange(
                new FailedJob
                {
                    Queue = "encoder",
                    Payload = "{}",
                    Exception = "boom",
                    FailedAt = DateTime.UtcNow.AddMinutes(-5),
                },
                new FailedJob
                {
                    Queue = "encoder",
                    Payload = "{}",
                    Exception = "boom2",
                    FailedAt = DateTime.UtcNow,
                }
            );
            context.SaveChanges();
        }

        List<FailedJobModel> failed = await _repository.GetFailedJobsAsync();

        failed.Should().HaveCount(2);
        failed[0].Exception.Should().Be("boom2");
    }

    [Fact]
    public async Task FindFailedJobAsync_ReturnsNull_WhenMissing()
    {
        (await _repository.FindFailedJobAsync(999)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteJobAsync_RemovesTheRow_AndReturnsItsSnapshot()
    {
        QueueJob job = AddJob("encoder", 1, "{\"id\":\"7\"}");

        QueueJobModel? deleted = await _repository.DeleteJobAsync(job.Id);

        deleted.Should().NotBeNull();
        deleted!.Payload.Should().Be("{\"id\":\"7\"}");

        using QueueContext context = _factory.CreateDbContext();
        (await context.QueueJobs.FindAsync(job.Id)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteJobAsync_ReturnsNull_WhenJobDoesNotExist()
    {
        (await _repository.DeleteJobAsync(12345)).Should().BeNull();
    }

    [Fact]
    public async Task UpdateJobPriorityAsync_UpdatesExistingJob_AndReturnsTrue()
    {
        QueueJob job = AddJob("encoder", 1, "{}");

        (await _repository.UpdateJobPriorityAsync(job.Id, 9)).Should().BeTrue();

        using QueueContext context = _factory.CreateDbContext();
        (await context.QueueJobs.FindAsync(job.Id))!.Priority.Should().Be(9);
    }

    [Fact]
    public async Task UpdateJobPriorityAsync_ReturnsFalse_WhenJobDoesNotExist()
    {
        (await _repository.UpdateJobPriorityAsync(999, 9)).Should().BeFalse();
    }

    [Fact]
    public async Task GetEncoderQueueCountsAsync_SplitsVideoAndMaintenance_PendingAndRunning()
    {
        AddJob("encoder", 4, "{\"$type\":\"VideoEncodeJob\"}");
        AddJob("encoder", 4, "{\"$type\":\"VideoEncodeJob\"}", DateTime.UtcNow);
        AddJob("encoder", 1, "{\"$type\":\"SpriteSheetUpgradeJob\"}");

        EncoderQueueCounts counts = await _repository.GetEncoderQueueCountsAsync();

        counts.VideoPending.Should().Be(1);
        counts.MaintenancePending.Should().Be(1);
        counts.RunningTotal.Should().Be(1);
    }

    [Fact]
    public async Task GetEncoderQueueDepthAsync_CountsTheEncoderFamilyOnly()
    {
        AddJob("encoder", 4, "{}");
        AddJob("encoder-cpu", 4, "{\"$type\":\"MusicEncodeJob\"}");
        AddJob("import", 4, "{}");

        (await _repository.GetEncoderQueueDepthAsync()).Should().Be(2);
    }

    [Fact]
    public async Task GetQueueJobCountAsync_CountsEveryQueueUnfiltered()
    {
        AddJob("encoder", 1, "{}");
        AddJob("import", 1, "{}");

        (await _repository.GetQueueJobCountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task GetFailedJobCountAsync_CountsEveryFailedJob()
    {
        using (QueueContext context = _factory.CreateDbContext())
        {
            context.FailedJobs.Add(
                new()
                {
                    Queue = "encoder",
                    Payload = "{}",
                    Exception = "boom",
                }
            );
            context.SaveChanges();
        }

        (await _repository.GetFailedJobCountAsync()).Should().Be(1);
    }

    private sealed class TestQueueContextFactory(DbContextOptions<QueueContext> options)
        : IDbContextFactory<QueueContext>
    {
        public QueueContext CreateDbContext() => new(options);
    }
}
