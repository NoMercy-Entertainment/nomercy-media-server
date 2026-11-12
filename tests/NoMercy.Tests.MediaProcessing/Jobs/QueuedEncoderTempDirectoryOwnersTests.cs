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
using NoMercy.Database.Models.Queue;
using NoMercy.Encoder.Orchestration;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.MediaProcessing.Jobs.MediaJobs.Support;
using NoMercy.Storage;
using NoMercyQueue;

namespace NoMercy.Tests.MediaProcessing.Jobs;

public class QueuedEncoderTempDirectoryOwnersTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private IDbContextFactory<QueueContext> _queueFactory = null!;

    public async Task InitializeAsync()
    {
        _connection = new("DataSource=:memory:");
        await _connection.OpenAsync();
        _queueFactory = new QueueFactory(_connection);

        await using QueueContext context = await _queueFactory.CreateDbContextAsync();
        await context.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private sealed class QueueFactory(SqliteConnection connection) : IDbContextFactory<QueueContext>
    {
        public QueueContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<QueueContext>().UseSqlite(connection).Options);
    }

    private sealed class NoMediaFactory : IDbContextFactory<MediaContext>
    {
        public MediaContext CreateDbContext() =>
            throw new InvalidOperationException("the media database must not be needed");
    }

    private async Task EnqueueAsync(string queue, string payload)
    {
        await using QueueContext context = await _queueFactory.CreateDbContextAsync();
        context.QueueJobs.Add(
            new()
            {
                Queue = queue,
                Priority = 4,
                Payload = payload,
                AvailableAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
            }
        );
        await context.SaveChangesAsync();
    }

    private static string CoordinatorPayload(string outputDirectory)
    {
        VideoEncodeJob job = new()
        {
            Coordinator = new(
                GroupTag: "group",
                TaskIds: [],
                Phase: CoordinatorPhase.WaitChildren,
                Pass1DispatchedAt: null,
                Pass2DispatchedAt: null,
                Pass1StatsPath: null,
                PresetId: Ulid.NewUlid(),
                ExpectedFinalCount: 1,
                OutputDirectory: outputDirectory
            ),
        };
        return SerializationHelper.Serialize(job);
    }

    [Fact]
    public async Task GetOwnedTempDirectories_RunningCoordinator_ReturnsItsMirroredFolder()
    {
        await EnqueueAsync("encoder", CoordinatorPayload("Frieren.(2023)/Frieren.S01E01"));

        QueuedEncoderTempDirectoryOwners owners = new(_queueFactory, new NoMediaFactory());
        IReadOnlyCollection<string> owned = await owners.GetOwnedTempDirectoriesAsync(
            CancellationToken.None
        );

        owned
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(Path.Combine(StoragePaths.TranscodeRoot, "Frieren.(2023)", "Frieren.S01E01"));
    }

    [Fact]
    public async Task GetOwnedTempDirectories_JobOnAnotherQueue_IsIgnored()
    {
        await EnqueueAsync("library", CoordinatorPayload("Frieren.(2023)/Frieren.S01E01"));

        QueuedEncoderTempDirectoryOwners owners = new(_queueFactory, new NoMediaFactory());
        IReadOnlyCollection<string> owned = await owners.GetOwnedTempDirectoriesAsync(
            CancellationToken.None
        );

        owned.Should().BeEmpty();
    }

    [Fact]
    public async Task GetOwnedTempDirectories_UnreadablePayload_Throws()
    {
        await EnqueueAsync("encoder", "{ not json");

        QueuedEncoderTempDirectoryOwners owners = new(_queueFactory, new NoMediaFactory());
        Func<Task> act = () => owners.GetOwnedTempDirectoriesAsync(CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
    }
}
