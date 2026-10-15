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

using Microsoft.EntityFrameworkCore;
using NoMercyQueue.Core;
using NoMercyQueue.Core.Interfaces;
using NoMercyQueue.Core.Models;
using NoMercyQueue.Sqlite.Entities;

namespace NoMercyQueue.Sqlite;

public class SqliteQueueContext : IQueueContext
{
    private readonly QueueDbContext _context;

    internal static readonly Func<
        QueueDbContext,
        byte,
        string,
        long?,
        DateTime,
        QueueJobEntity?
    > ReserveJobQuery = EF.CompileQuery(
        (QueueDbContext context, byte maxAttempts, string name, long? currentJobId, DateTime now) =>
            context
                .QueueJobs.Where(j =>
                    j.ReservedAt == null && j.Attempts < maxAttempts && j.AvailableAt <= now
                )
                .Where(j => currentJobId == null)
                .Where(j => j.Queue == name)
                .OrderByDescending(j => j.Priority)
                .ThenBy(j => j.Id)
                .FirstOrDefault()
    );

    // Narrowed by hash, decided on the payload: the hash is what the index can serve,
    // and the payload comparison is what dedup has always meant, so a collision costs
    // one extra row read rather than a silently swallowed job.
    internal static readonly Func<QueueDbContext, string, string, bool> ExistsQuery =
        EF.CompileQuery(
            (QueueDbContext context, string payloadHash, string payloadString) =>
                context.QueueJobs.Any(j =>
                    j.PayloadHash == payloadHash && j.Payload == payloadString
                )
        );

    internal SqliteQueueContext(QueueDbContext context)
    {
        _context = context;
    }

    public void AddJob(QueueJobModel job)
    {
        QueueJobEntity entity = new()
        {
            Priority = job.Priority,
            Queue = job.Queue,
            Payload = job.Payload,
            PayloadHash = QueuePayloadHash.For(job.Payload),
            OriginalPayloadHash = QueuePayloadHash.Original(job.OriginalPayloadHash, job.Payload),
            SharedInputKey = job.SharedInputKey,
            Attempts = job.Attempts,
            Interruptions = job.Interruptions,
            ReservedAt = job.ReservedAt,
            AvailableAt = job.AvailableAt,
            CreatedAt = job.CreatedAt,
            ParentJobId = job.ParentJobId,
            GroupTag = job.GroupTag,
        };
        _context.QueueJobs.Add(entity);
        SaveAndClear();
        job.Id = entity.Id;
    }

    public void RemoveJob(QueueJobModel job)
    {
        QueueJobEntity? entity = _context.QueueJobs.Find(job.Id);
        if (entity == null)
        {
            entity = new()
            {
                Id = job.Id,
                Payload = job.Payload,
                Queue = job.Queue,
            };
            _context.QueueJobs.Attach(entity);
        }
        _context.QueueJobs.Remove(entity);
        SaveAndClear();
    }

    public QueueJobModel? GetNextJob(
        string queueName,
        byte maxAttempts,
        long? currentJobId,
        DateTime now
    )
    {
        if (string.IsNullOrEmpty(queueName))
        {
            QueueJobEntity? anyJob = _context
                .QueueJobs.OrderByDescending(j => j.Priority)
                .ThenBy(j => j.Id)
                .FirstOrDefault();
            return anyJob == null ? null : ToModel(anyJob);
        }

        QueueJobEntity? job = ReserveJobQuery(_context, maxAttempts, queueName, currentJobId, now);
        if (job == null)
            return null;

        return ToModel(job);
    }

    public QueueJobModel? FindJob(int id)
    {
        QueueJobEntity? job = _context.QueueJobs.Find(id);
        return job == null ? null : ToModel(job);
    }

    public bool JobExists(string payload)
    {
        return ExistsQuery(_context, QueuePayloadHash.For(payload), payload);
    }

    public QueueJobModel? FindJobByPayloadHash(string payloadHash)
    {
        QueueJobEntity? job =
            _context
                .QueueJobs.AsNoTracking()
                .FirstOrDefault(entity => entity.OriginalPayloadHash == payloadHash)
            ?? _context
                .QueueJobs.AsNoTracking()
                .FirstOrDefault(entity => entity.PayloadHash == payloadHash);

        return job == null ? null : ToModel(job);
    }

    public FailedJobModel? FindFailedJobByPayloadHash(string payloadHash)
    {
        // Failed rows from before the column existed have it empty and are
        // matched on their payload. The failed table is small.
        FailedJobEntity? failed =
            _context
                .FailedJobs.AsNoTracking()
                .FirstOrDefault(entity => entity.OriginalPayloadHash == payloadHash)
            ?? _context
                .FailedJobs.AsNoTracking()
                .Where(entity => entity.OriginalPayloadHash == string.Empty)
                .AsEnumerable()
                .FirstOrDefault(entity => QueuePayloadHash.For(entity.Payload) == payloadHash);

        return failed == null ? null : ToFailedModel(failed);
    }

    public void UpdateJob(QueueJobModel job)
    {
        QueueJobEntity? entity = _context.QueueJobs.Find(job.Id);
        if (entity == null)
            return;

        entity.Priority = job.Priority;
        entity.Queue = job.Queue;
        entity.Attempts = job.Attempts;
        entity.Interruptions = job.Interruptions;
        entity.ReservedAt = job.ReservedAt;
        entity.AvailableAt = job.AvailableAt;
        SaveAndClear();
    }

    public void UpdateJobPayload(int jobId, string newPayload, DateTime availableAt)
    {
        QueueJobEntity? entity = _context.QueueJobs.Find(jobId);
        if (entity == null)
            return;

        entity.Payload = newPayload;
        entity.PayloadHash = QueuePayloadHash.For(newPayload);
        entity.ReservedAt = null;
        entity.AvailableAt = availableAt;
        entity.Attempts = 0;
        SaveAndClear();
    }

    public void ResetAllReservedJobs()
    {
        foreach (QueueJobEntity job in _context.QueueJobs.Where(j => j.ReservedAt != null))
        {
            job.ReservedAt = null;
            job.Attempts = (byte)Math.Max(0, job.Attempts - 1);
            job.Interruptions = (byte)Math.Min(byte.MaxValue, job.Interruptions + 1);
        }
        SaveAndClear();
    }

    public IReadOnlyList<QueueJobModel> GetReservedJobsOlderThan(DateTime cutoffUtc)
    {
        return _context
            .QueueJobs.AsNoTracking()
            .Where(j => j.ReservedAt != null && j.ReservedAt < cutoffUtc)
            .ToList()
            .Select(e => new QueueJobModel
            {
                Id = e.Id,
                Priority = e.Priority,
                Queue = e.Queue,
                Payload = e.Payload,
                Attempts = e.Attempts,
                Interruptions = e.Interruptions,
                ReservedAt = e.ReservedAt,
                AvailableAt = e.AvailableAt,
                CreatedAt = e.CreatedAt,
                ParentJobId = e.ParentJobId,
                GroupTag = e.GroupTag,
            })
            .ToList();
    }

    public IReadOnlyList<QueueJobModel> GetStrandedJobs(byte maxAttempts, byte maxInterruptions)
    {
        return _context
            .QueueJobs.AsNoTracking()
            .Where(j =>
                j.ReservedAt == null
                && (j.Attempts >= maxAttempts || j.Interruptions >= maxInterruptions)
            )
            .ToList()
            .Select(e => new QueueJobModel
            {
                Id = e.Id,
                Priority = e.Priority,
                Queue = e.Queue,
                Payload = e.Payload,
                Attempts = e.Attempts,
                Interruptions = e.Interruptions,
                ReservedAt = e.ReservedAt,
                AvailableAt = e.AvailableAt,
                CreatedAt = e.CreatedAt,
                ParentJobId = e.ParentJobId,
                GroupTag = e.GroupTag,
            })
            .ToList();
    }

    public bool IsParentFailed(int parentJobId)
    {
        return _context.FailedJobs.AsNoTracking().Any(f => f.ParentJobId == parentJobId);
    }

    public void AddFailedJob(FailedJobModel failedJob)
    {
        FailedJobEntity entity = new()
        {
            Uuid = failedJob.Uuid,
            Connection = failedJob.Connection,
            Queue = failedJob.Queue,
            Payload = failedJob.Payload,
            Exception = failedJob.Exception,
            FailedAt = failedJob.FailedAt,
            ParentJobId = failedJob.ParentJobId,
            OriginalPayloadHash = failedJob.OriginalPayloadHash,
        };
        _context.FailedJobs.Add(entity);
    }

    public void RemoveFailedJob(FailedJobModel failedJob)
    {
        FailedJobEntity? entity = _context.FailedJobs.Find(failedJob.Id);
        if (entity != null)
        {
            _context.FailedJobs.Remove(entity);
        }
    }

    public void AddFailedJobAndRemoveJob(FailedJobModel failedJob, QueueJobModel job)
    {
        FailedJobEntity failedEntity = new()
        {
            Uuid = failedJob.Uuid,
            Connection = failedJob.Connection,
            Queue = failedJob.Queue,
            Payload = failedJob.Payload,
            Exception = failedJob.Exception,
            FailedAt = failedJob.FailedAt,
            ParentJobId = failedJob.ParentJobId,
            OriginalPayloadHash = QueuePayloadHash.Original(
                string.IsNullOrEmpty(failedJob.OriginalPayloadHash)
                    ? job.OriginalPayloadHash
                    : failedJob.OriginalPayloadHash,
                failedJob.Payload
            ),
        };
        _context.FailedJobs.Add(failedEntity);

        QueueJobEntity? jobEntity = _context.QueueJobs.Find(job.Id);
        if (jobEntity == null)
        {
            jobEntity = new()
            {
                Id = job.Id,
                Payload = job.Payload,
                Queue = job.Queue,
            };
            _context.QueueJobs.Attach(jobEntity);
        }
        _context.QueueJobs.Remove(jobEntity);

        SaveAndClear();
        failedJob.Id = failedEntity.Id;
    }

    public FailedJobModel? FindFailedJob(int id)
    {
        FailedJobEntity? entity = _context.FailedJobs.Find((long)id);
        return entity == null ? null : ToFailedModel(entity);
    }

    public IReadOnlyList<FailedJobModel> GetFailedJobs(long? failedJobId = null)
    {
        IQueryable<FailedJobEntity> query = _context.FailedJobs;
        if (failedJobId.HasValue)
            query = query.Where(j => j.Id == failedJobId.Value);

        return query
            .Select(j => new FailedJobModel
            {
                Id = j.Id,
                Uuid = j.Uuid,
                Connection = j.Connection,
                Queue = j.Queue,
                Payload = j.Payload,
                Exception = j.Exception,
                FailedAt = j.FailedAt,
                ParentJobId = j.ParentJobId,
                OriginalPayloadHash = j.OriginalPayloadHash,
            })
            .ToList();
    }

    public IReadOnlyList<CronJobModel> GetEnabledCronJobs()
    {
        return _context
            .CronJobs.Where(c => c.IsEnabled)
            .Select(c => new CronJobModel
            {
                Id = c.Id,
                Name = c.Name,
                CronExpression = c.CronExpression,
                JobType = c.JobType,
                Parameters = c.Parameters,
                IsEnabled = c.IsEnabled,
                LastRun = c.LastRun,
                NextRun = c.NextRun,
            })
            .ToList();
    }

    public CronJobModel? FindCronJobByName(string name)
    {
        CronJobEntity? entity = _context.CronJobs.FirstOrDefault(c => c.Name == name);
        return entity == null ? null : ToCronModel(entity);
    }

    public void AddCronJob(CronJobModel cronJob)
    {
        CronJobEntity entity = new()
        {
            Name = cronJob.Name,
            CronExpression = cronJob.CronExpression,
            JobType = cronJob.JobType,
            Parameters = cronJob.Parameters,
            IsEnabled = cronJob.IsEnabled,
            LastRun = cronJob.LastRun,
            NextRun = cronJob.NextRun,
        };
        _context.CronJobs.Add(entity);
        SaveAndClear();
    }

    public void UpdateCronJob(CronJobModel cronJob)
    {
        CronJobEntity? entity = _context.CronJobs.Find(cronJob.Id);
        if (entity == null)
            return;

        entity.CronExpression = cronJob.CronExpression;
        entity.IsEnabled = cronJob.IsEnabled;
        entity.LastRun = cronJob.LastRun;
        entity.NextRun = cronJob.NextRun;
        SaveAndClear();
    }

    public void RemoveCronJob(CronJobModel cronJob)
    {
        CronJobEntity? entity = _context.CronJobs.Find(cronJob.Id);
        if (entity != null)
        {
            _context.CronJobs.Remove(entity);
            SaveAndClear();
        }
    }

    public void SaveChanges()
    {
        SaveAndClear();
    }

    public void Dispose()
    {
        _context.Dispose();
    }

    private void SaveAndClear()
    {
        if (_context.ChangeTracker.HasChanges())
        {
            _context.SaveChanges();
            _context.ChangeTracker.Clear();
        }
    }

    private static QueueJobModel ToModel(QueueJobEntity entity)
    {
        return new()
        {
            Id = entity.Id,
            Priority = entity.Priority,
            Queue = entity.Queue,
            Payload = entity.Payload,
            OriginalPayloadHash = entity.OriginalPayloadHash,
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

    private static FailedJobModel ToFailedModel(FailedJobEntity entity)
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
            OriginalPayloadHash = entity.OriginalPayloadHash,
        };
    }

    private static CronJobModel ToCronModel(CronJobEntity entity)
    {
        return new()
        {
            Id = entity.Id,
            Name = entity.Name,
            CronExpression = entity.CronExpression,
            JobType = entity.JobType,
            Parameters = entity.Parameters,
            IsEnabled = entity.IsEnabled,
            LastRun = entity.LastRun,
            NextRun = entity.NextRun,
        };
    }
}
