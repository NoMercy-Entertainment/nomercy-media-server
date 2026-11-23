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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NoMercy.Database;
using NoMercy.Database.Models.Queue;

namespace NoMercy.Tests.Database.Migrations;

/// <summary>
/// A server that updates with analysis jobs still waiting (#689). The job
/// class is gone, so every such row could only fail: the update takes them out
/// of the queue, out of the failed jobs and out of the schedules, and leaves
/// every other job where it was.
/// </summary>
[Trait("Category", "Integration")]
public class RemoveMusicAnalysisJobsMigrationTests : IDisposable
{
    private const string PreviousMigration = "20261015191845_QueueJobOriginalPayloadHash";

    private const string AnalysisPayload =
        """{"$type":"NoMercy.MediaProcessing.Jobs.MediaJobs.MusicAnalysisJob, NoMercy.MediaProcessing","trackId":"0b0e1f9e-6d0c-4a54-9c7e-0d1f4f3f3a11"}""";

    // Another job that happens to carry the words, to prove the match is on the job's type.
    private const string EncodePayload =
        """{"$type":"NoMercy.MediaProcessing.Jobs.MediaJobs.MusicEncodeJob, NoMercy.MediaProcessing","note":"MusicAnalysisJob"}""";

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"nm_remove_music_analysis_jobs_migration_{Guid.NewGuid():N}.db"
    );

    public void Dispose()
    {
        // Pooled handles keep the file locked on Windows past Dispose().
        SqliteConnection.ClearAllPools();

        if (File.Exists(_databasePath))
            File.Delete(_databasePath);

        GC.SuppressFinalize(this);
    }

    private QueueContext OpenContext()
    {
        DbContextOptionsBuilder<QueueContext> builder = new();
        builder.UseSqlite($"Data Source={_databasePath}");

        return new(builder.Options);
    }

    [Fact]
    public async Task WaitingAndFailedAnalysisJobs_AreRemoved_AndEveryOtherJobStays()
    {
        await using (QueueContext oldContext = OpenContext())
        {
            await oldContext.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            oldContext.QueueJobs.AddRange(
                new QueueJob
                {
                    Queue = "music",
                    Payload = AnalysisPayload,
                    PayloadHash = "analysis",
                },
                new QueueJob
                {
                    Queue = "music",
                    Payload = EncodePayload,
                    PayloadHash = "encode",
                }
            );
            oldContext.FailedJobs.AddRange(
                new FailedJob
                {
                    Uuid = Guid.NewGuid(),
                    Queue = "music",
                    Payload = AnalysisPayload,
                    Exception = "ffmpeg exited with 1",
                },
                new FailedJob
                {
                    Uuid = Guid.NewGuid(),
                    Queue = "music",
                    Payload = EncodePayload,
                    Exception = "ffmpeg exited with 1",
                }
            );
            await oldContext.SaveChangesAsync();
        }

        await using (QueueContext upgradeContext = OpenContext())
        {
            await upgradeContext.Database.MigrateAsync();
        }

        await using QueueContext readContext = OpenContext();

        QueueJob waiting = await readContext.QueueJobs.AsNoTracking().SingleAsync();
        waiting.Payload.Should().Be(EncodePayload);

        FailedJob failed = await readContext.FailedJobs.AsNoTracking().SingleAsync();
        failed.Payload.Should().Be(EncodePayload);
    }

    [Fact]
    public async Task TheTwoAnalysisSchedules_AreRemoved_AndEveryOtherScheduleStays()
    {
        await using (QueueContext oldContext = OpenContext())
        {
            await oldContext.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            oldContext.CronJobs.AddRange(
                new CronJob
                {
                    Name = "Audio Analysis Sweep",
                    CronExpression = "0 * * * *",
                    JobType = "audio-analysis-sweep",
                },
                new CronJob
                {
                    Name = "Derived Audio Eviction",
                    CronExpression = "0 * * * *",
                    JobType = "derived-audio-eviction",
                },
                new CronJob
                {
                    Name = "Certificate Renewal",
                    CronExpression = "0 3 * * *",
                    JobType = "certificate-renewal",
                }
            );
            await oldContext.SaveChangesAsync();
        }

        await using (QueueContext upgradeContext = OpenContext())
        {
            await upgradeContext.Database.MigrateAsync();
        }

        await using QueueContext readContext = OpenContext();

        CronJob kept = await readContext.CronJobs.AsNoTracking().SingleAsync();
        kept.JobType.Should().Be("certificate-renewal");
    }
}
