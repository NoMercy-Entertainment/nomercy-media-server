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

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Database.Models.Queue;
using NoMercyQueue.Core;
using NoMercyQueue.Core.Models;

namespace NoMercy.Queue.MediaServer.Repositories;

/// <inheritdoc cref="IQueueTaskRepository"/>
public class QueueTaskRepository(IDbContextFactory<QueueContext> queueContextFactory)
    : IQueueTaskRepository
{
    // The encoder queue is shared with the music encoder, which runs on its own
    // 'encoder-cpu' lane rather than 'encoder' — so every listing here has to
    // read both, scoped to MusicEncodeJob payloads only, or a busy album import
    // starves the video panel of its own queue depth.
    private static readonly Expression<Func<QueueJob, bool>> IsEncoderFamilyJob = job =>
        job.Queue == QueueNames.Encoder
        || (job.Queue == QueueNames.EncoderCpu && job.Payload.Contains("MusicEncodeJob"));

    public async Task<List<QueueJobModel>> GetRecentJobsAsync(
        int limit,
        CancellationToken cancellationToken = default
    )
    {
        await using QueueContext context = await queueContextFactory.CreateDbContextAsync(
            cancellationToken
        );

        List<QueueJob> jobs = await context
            .QueueJobs.AsNoTracking()
            .OrderByDescending(job => job.Priority)
            .ThenBy(job => job.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return [.. jobs.Select(ToModel)];
    }

    public async Task<List<int>> GetEncoderPrioritiesAsync(
        CancellationToken cancellationToken = default
    )
    {
        await using QueueContext context = await queueContextFactory.CreateDbContextAsync(
            cancellationToken
        );

        return await context
            .QueueJobs.AsNoTracking()
            .Where(IsEncoderFamilyJob)
            .Select(job => job.Priority)
            .Distinct()
            .OrderByDescending(priority => priority)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<QueueJobModel>> GetEncoderJobsByPriorityAsync(
        int priority,
        int limit,
        CancellationToken cancellationToken = default
    )
    {
        await using QueueContext context = await queueContextFactory.CreateDbContextAsync(
            cancellationToken
        );

        List<QueueJob> jobs = await context
            .QueueJobs.AsNoTracking()
            .Where(IsEncoderFamilyJob)
            .Where(job => job.Priority == priority)
            .OrderBy(job => job.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return [.. jobs.Select(ToModel)];
    }

    public async Task<List<QueueJobModel>> GetRunningEncoderJobsAsync(
        int limit,
        CancellationToken cancellationToken = default
    )
    {
        await using QueueContext context = await queueContextFactory.CreateDbContextAsync(
            cancellationToken
        );

        List<QueueJob> jobs = await context
            .QueueJobs.AsNoTracking()
            .Where(IsEncoderFamilyJob)
            .Where(job => job.ReservedAt != null)
            .OrderBy(job => job.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return [.. jobs.Select(ToModel)];
    }

    public async Task<Dictionary<Guid, int>> CountQueuedTracksByReleaseAsync(
        CancellationToken cancellationToken = default
    )
    {
        await using QueueContext context = await queueContextFactory.CreateDbContextAsync(
            cancellationToken
        );

        List<ReleaseTrackCountRow> counts = await context
            .Database.SqlQueryRaw<ReleaseTrackCountRow>(
                """
                SELECT json_extract(Payload, '$.releaseId') AS ReleaseId,
                       COUNT(*) AS Remaining
                FROM QueueJobs
                WHERE Queue = 'encoder-cpu'
                  AND Payload LIKE '%MusicEncodeJob%'
                GROUP BY json_extract(Payload, '$.releaseId')
                """
            )
            .ToListAsync(cancellationToken);

        Dictionary<Guid, int> byRelease = [];
        foreach (ReleaseTrackCountRow count in counts)
            if (Guid.TryParse(count.ReleaseId, out Guid releaseId))
                byRelease[releaseId] = count.Remaining;

        return byRelease;
    }

    public async Task<int> CountQueuedMusicAnalysisAsync(
        CancellationToken cancellationToken = default
    )
    {
        await using QueueContext context = await queueContextFactory.CreateDbContextAsync(
            cancellationToken
        );

        return await context
            .QueueJobs.AsNoTracking()
            .Where(job => job.Queue == QueueNames.Music && job.Payload.Contains("MusicAnalysisJob"))
            .CountAsync(cancellationToken);
    }

    public async Task<bool> QueueExistsAsync(
        string queueName,
        CancellationToken cancellationToken = default
    )
    {
        await using QueueContext context = await queueContextFactory.CreateDbContextAsync(
            cancellationToken
        );

        return await context
            .QueueJobs.AsNoTracking()
            .AnyAsync(job => job.Queue == queueName, cancellationToken);
    }

    public async Task<List<QueueJobModel>> GetJobsForQueueAsync(
        string queueName,
        CancellationToken cancellationToken = default
    )
    {
        await using QueueContext context = await queueContextFactory.CreateDbContextAsync(
            cancellationToken
        );

        List<QueueJob> jobs = await context
            .QueueJobs.AsNoTracking()
            .Where(job => job.Queue == queueName)
            .OrderByDescending(job => job.Priority)
            .ThenBy(job => job.CreatedAt)
            .ThenBy(job => job.Id)
            .ToListAsync(cancellationToken);

        return [.. jobs.Select(ToModel)];
    }

    public async Task SetPrioritiesAsync(
        IReadOnlyDictionary<int, int> priorityByJobId,
        CancellationToken cancellationToken = default
    )
    {
        if (priorityByJobId.Count == 0)
            return;

        await using QueueContext context = await queueContextFactory.CreateDbContextAsync(
            cancellationToken
        );

        List<int> ids = [.. priorityByJobId.Keys];
        List<QueueJob> jobs = await context
            .QueueJobs.Where(job => ids.Contains(job.Id))
            .ToListAsync(cancellationToken);

        foreach (QueueJob job in jobs)
            job.Priority = priorityByJobId[job.Id];

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<FailedJobModel>> GetFailedJobsAsync(
        CancellationToken cancellationToken = default
    )
    {
        await using QueueContext context = await queueContextFactory.CreateDbContextAsync(
            cancellationToken
        );

        List<FailedJob> failedJobs = await context
            .FailedJobs.AsNoTracking()
            .OrderByDescending(job => job.FailedAt)
            .ToListAsync(cancellationToken);

        return [.. failedJobs.Select(ToFailedModel)];
    }

    public async Task<FailedJobModel?> FindFailedJobAsync(
        long id,
        CancellationToken cancellationToken = default
    )
    {
        await using QueueContext context = await queueContextFactory.CreateDbContextAsync(
            cancellationToken
        );

        FailedJob? failedJob = await context
            .FailedJobs.AsNoTracking()
            .FirstOrDefaultAsync(job => job.Id == id, cancellationToken);

        return failedJob is null ? null : ToFailedModel(failedJob);
    }

    public async Task<QueueJobModel?> DeleteJobAsync(
        int id,
        CancellationToken cancellationToken = default
    )
    {
        await using QueueContext context = await queueContextFactory.CreateDbContextAsync(
            cancellationToken
        );

        QueueJob? job = await context.QueueJobs.FirstOrDefaultAsync(
            j => j.Id == id,
            cancellationToken
        );
        if (job is null)
            return null;

        QueueJobModel model = ToModel(job);

        context.QueueJobs.Remove(job);
        await context.SaveChangesAsync(cancellationToken);

        return model;
    }

    public async Task<bool> UpdateJobPriorityAsync(
        int id,
        int priority,
        CancellationToken cancellationToken = default
    )
    {
        await using QueueContext context = await queueContextFactory.CreateDbContextAsync(
            cancellationToken
        );

        QueueJob? job = await context.QueueJobs.FirstOrDefaultAsync(
            j => j.Id == id,
            cancellationToken
        );
        if (job is null)
            return false;

        job.Priority = priority;
        await context.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<EncoderQueueCounts> GetEncoderQueueCountsAsync(
        CancellationToken cancellationToken = default
    )
    {
        await using QueueContext context = await queueContextFactory.CreateDbContextAsync(
            cancellationToken
        );

        List<QueueKindCountRow> counts = await context
            .Database.SqlQueryRaw<QueueKindCountRow>(
                """
                SELECT CASE
                         WHEN Payload LIKE '%VideoEncodeJob%' THEN 'video'
                         WHEN Payload LIKE '%MusicEncodeJob%' THEN 'music'
                         ELSE 'maintenance'
                       END AS Kind,
                       SUM(CASE WHEN ReservedAt IS NULL THEN 1 ELSE 0 END) AS Pending,
                       SUM(CASE WHEN ReservedAt IS NULL THEN 0 ELSE 1 END) AS Running
                FROM QueueJobs
                WHERE Queue = 'encoder'
                   OR (Queue = 'encoder-cpu' AND Payload LIKE '%MusicEncodeJob%')
                GROUP BY Kind
                """
            )
            .ToListAsync(cancellationToken);

        int videoPending = counts.FirstOrDefault(row => row.Kind == "video")?.Pending ?? 0;
        int maintenancePending =
            counts.FirstOrDefault(row => row.Kind == "maintenance")?.Pending ?? 0;
        int runningTotal = counts.Sum(row => row.Running);

        return new(videoPending, maintenancePending, runningTotal);
    }

    public async Task<int> GetEncoderQueueDepthAsync(CancellationToken cancellationToken = default)
    {
        await using QueueContext context = await queueContextFactory.CreateDbContextAsync(
            cancellationToken
        );

        return await context
            .QueueJobs.AsNoTracking()
            .Where(IsEncoderFamilyJob)
            .CountAsync(cancellationToken);
    }

    public async Task<int> GetQueueJobCountAsync(CancellationToken cancellationToken = default)
    {
        await using QueueContext context = await queueContextFactory.CreateDbContextAsync(
            cancellationToken
        );

        return await context.QueueJobs.AsNoTracking().CountAsync(cancellationToken);
    }

    public async Task<int> GetFailedJobCountAsync(CancellationToken cancellationToken = default)
    {
        await using QueueContext context = await queueContextFactory.CreateDbContextAsync(
            cancellationToken
        );

        return await context.FailedJobs.AsNoTracking().CountAsync(cancellationToken);
    }

    private static QueueJobModel ToModel(QueueJob entity)
    {
        return new()
        {
            Id = entity.Id,
            Priority = entity.Priority,
            Queue = entity.Queue,
            Payload = entity.Payload,
            SharedInputKey = entity.SharedInputKey,
            Attempts = entity.Attempts,
            Interruptions = entity.Interruptions,
            ReservedAt = entity.ReservedAt,
            AvailableAt = entity.AvailableAt,
            CreatedAt = entity.CreatedAt,
            ParentJobId = entity.ParentJobId,
            GroupTag = entity.GroupTag,
        };
    }

    private static FailedJobModel ToFailedModel(FailedJob entity)
    {
        return new()
        {
            Id = entity.Id,
            Uuid = entity.Uuid,
            Connection = entity.Connection,
            Queue = entity.Queue,
            Payload = entity.Payload,
            Exception = entity.Exception,
            FailedAt = entity.FailedAt,
            ParentJobId = entity.ParentJobId,
        };
    }

    /// <summary>Tracks of one release still on the encoder queue, counted in SQL.</summary>
    private sealed class ReleaseTrackCountRow
    {
        public string? ReleaseId { get; set; }
        public int Remaining { get; set; }
    }

    /// <summary>
    /// How much encoder work of one kind is waiting and how much is in flight.
    /// </summary>
    private sealed class QueueKindCountRow
    {
        public string? Kind { get; set; }
        public int Pending { get; set; }
        public int Running { get; set; }
    }
}
