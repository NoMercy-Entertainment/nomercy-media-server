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

using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NoMercyQueue.Core.Interfaces;
using NoMercyQueue.Core.Models;

namespace NoMercyQueue;

public class JobQueue(
    IQueueContext context,
    byte maxAttempts = 3,
    ILogger<JobQueue>? logger = null,
    byte maxInterruptions = 10
)
{
    private const int MaxDbRetryAttempts = 5;
    private const int BaseRetryDelayMs = 2000;
    private const int MaxJitterMs = 500;

    private readonly object _writeLock = new();

    public byte MaxAttempts => maxAttempts;

    /// <summary>
    /// Signalled once per <see cref="Enqueue"/> call so idle workers wake
    /// immediately instead of waiting out a fixed poll interval.
    /// </summary>
    internal readonly SemaphoreSlim WorkAvailable = new(0);

    public void ResetAllReservedJobs()
    {
        lock (_writeLock)
        {
            context.ResetAllReservedJobs();
        }
    }

    /// <summary>
    /// Queue a job, and say which one it became.
    ///
    /// <para>
    /// Null when an identical payload is already queued. That is not a failure -
    /// asking twice for the same work is how a retry behaves - but a caller that
    /// holds a file until the job lands has to be able to tell "queued as 41"
    /// from "already queued, and I have no id for it".
    /// </para>
    /// </summary>
    public int? Enqueue(QueueJobModel queueJob)
    {
        lock (_writeLock)
        {
            bool exists = context.JobExists(queueJob.Payload);
            if (exists)
                return null;

            context.AddJob(queueJob);
        }

        WorkAvailable.Release();

        return queueJob.Id;
    }

    public QueueJobModel? Dequeue()
    {
        lock (_writeLock)
        {
            QueueJobModel? job = context.GetNextJob("", 255, null, DateTime.UtcNow);
            if (job == null)
                return job;

            context.RemoveJob(job);

            return job;
        }
    }

    public QueueJobModel? ReserveJob(string name, long? currentJobId, int attempt = 0)
    {
        try
        {
            lock (_writeLock)
            {
                QueueJobModel? job = context.GetNextJob(
                    name,
                    maxAttempts,
                    currentJobId,
                    DateTime.UtcNow
                );

                if (job == null)
                    return job;

                // Child jobs whose coordinator has already failed should not
                // run — they would produce orphaned output. Move them directly
                // to FailedJobs with a synthetic exception so the dashboard
                // shows "failed-by-parent" rather than silently dropping them.
                if (job.ParentJobId.HasValue && context.IsParentFailed(job.ParentJobId.Value))
                {
                    FailedJobModel skipped = new()
                    {
                        Uuid = Guid.NewGuid(),
                        Connection = "default",
                        Queue = job.Queue,
                        Payload = job.Payload,
                        ParentJobId = job.Id,
                        Exception =
                            $"{{\"Message\":\"Skipped: parent job {job.ParentJobId} failed\"}}",
                        FailedAt = DateTime.UtcNow,
                    };
                    context.AddFailedJobAndRemoveJob(skipped, job);
                    return null;
                }

                job.ReservedAt = DateTime.UtcNow;
                job.Attempts++;

                context.UpdateJob(job);

                return job;
            }
        }
        catch (Exception e)
        {
            if (e.Source == "Microsoft.EntityFrameworkCore.Relational")
            {
                logger?.LogDebug(e, "Queue DB contention reserving a job on {Queue}", name);
                return null;
            }
            if (attempt < MaxDbRetryAttempts)
            {
                Thread.Sleep(BaseRetryDelayMs + Random.Shared.Next(MaxJitterMs));
                return ReserveJob(name, currentJobId, attempt + 1);
            }

            logger?.LogError(e, "Failed to reserve a job on {Queue}", name);
        }

        return null;
    }

    public void FailJob(QueueJobModel queueJob, Exception exception, int attempt = 0)
    {
        try
        {
            lock (_writeLock)
            {
                queueJob.ReservedAt = null;

                if (queueJob.Attempts >= maxAttempts)
                {
                    FailedJobModel failedJob = new()
                    {
                        Uuid = Guid.NewGuid(),
                        Connection = "default",
                        Queue = queueJob.Queue,
                        Payload = queueJob.Payload,
                        ParentJobId = queueJob.Id,
                        Exception = JsonConvert.SerializeObject(
                            exception.InnerException ?? exception
                        ),
                        FailedAt = DateTime.UtcNow,
                    };

                    context.AddFailedJobAndRemoveJob(failedJob, queueJob);
                    FailParentChain(queueJob, "job.child_failed");
                }
                else
                {
                    // Released with no delay, a retried job was eligible again
                    // the instant the next worker polled — a transient failure
                    // (a MusicBrainz 503 under load) burned all 3 attempts back
                    // to back against a provider that hadn't had time to
                    // recover, guaranteeing the same outcome every time.
                    queueJob.AvailableAt = DateTime.UtcNow.AddSeconds(
                        Math.Min(60, Math.Pow(2, queueJob.Attempts) * 2)
                    );
                    context.UpdateJob(queueJob);
                }

                context.SaveChanges();
            }
        }
        catch (Exception e)
        {
            if (e.Source == "Microsoft.EntityFrameworkCore.Relational")
            {
                logger?.LogDebug(e, "Queue DB contention failing job {JobId}", queueJob.Id);
                return;
            }
            if (attempt < MaxDbRetryAttempts)
            {
                Thread.Sleep(BaseRetryDelayMs + Random.Shared.Next(MaxJitterMs));
                FailJob(queueJob, exception, attempt + 1);
            }
            else
            {
                logger?.LogError(e, "Failed to record job failure for {JobId}", queueJob.Id);
            }
        }
    }

    /// <summary>
    /// Dead-letters every row the reserve query can never hand out again —
    /// unreserved and already at <c>maxAttempts</c> — and returns how many were
    /// removed.
    /// <para>Nothing else in the engine can see these rows. The reserve
    /// predicate skips them by definition and both orphan passes only scan rows
    /// that are still RESERVED, so a row that burns its last attempt without
    /// going through <see cref="FailJob"/> stays in the queue forever. A
    /// mid-encode restart does exactly that: the boot pass clears
    /// <c>ReservedAt</c> and leaves <c>Attempts</c> where it was, so three
    /// restarts over the life of one encode strand its row silently — no
    /// failure recorded, no log line, and it keeps the queue position it was
    /// dispatched into. That is what put a season's first episode permanently
    /// at the head of the encoder queue while the rest of the season encoded
    /// past it.</para>
    /// </summary>
    public int FailStrandedJobs()
    {
        try
        {
            lock (_writeLock)
            {
                IReadOnlyList<QueueJobModel> stranded = context.GetStrandedJobs(
                    maxAttempts,
                    maxInterruptions
                );
                if (stranded.Count == 0)
                    return 0;

                foreach (QueueJobModel job in stranded)
                {
                    FailedJobModel failedJob = new()
                    {
                        Uuid = Guid.NewGuid(),
                        Connection = "default",
                        Queue = job.Queue,
                        Payload = job.Payload,
                        ParentJobId = job.Id,
                        Exception =
                            $"{{\"Message\":\"Stranded: {job.Attempts} attempts and {job.Interruptions} interruptions used, never released\"}}",
                        FailedAt = DateTime.UtcNow,
                    };

                    context.AddFailedJobAndRemoveJob(failedJob, job);
                    FailParentChain(job, "job.child_stranded");

                    logger?.LogWarning(
                        "Dead-lettered stranded job {JobId} on queue {Queue} — {Attempts} attempts used and no reservation held",
                        job.Id,
                        job.Queue,
                        job.Attempts
                    );
                }

                context.SaveChanges();
                return stranded.Count;
            }
        }
        catch (Exception e)
        {
            logger?.LogError(e, "Failed to sweep stranded jobs");
            return 0;
        }
    }

    /// <summary>
    /// Dead-letters the coordinator that dispatched <paramref name="job"/>, and
    /// its coordinator in turn.
    /// <para>Parent-to-child propagation already existed — <c>ReserveJob</c>
    /// skips a child whose parent has failed. The other direction did not, and
    /// a coordinator has no other way to learn that a child is never coming: it
    /// waits on rows in <c>EncodeTaskOutcomes</c>, and a child that dies writes
    /// none. So it re-queued itself every poll interval forever, holding its
    /// place at the head of the queue and reporting itself as merely
    /// queued.</para>
    /// <para>Called with <c>_writeLock</c> held; the caller commits.</para>
    /// </summary>
    private void FailParentChain(QueueJobModel job, string reason)
    {
        int? parentId = job.ParentJobId;
        HashSet<int> seen = [job.Id];

        while (parentId.HasValue && seen.Add(parentId.Value))
        {
            QueueJobModel? parent = context.FindJob(parentId.Value);
            if (parent is null)
                return;

            context.AddFailedJobAndRemoveJob(
                new()
                {
                    Uuid = Guid.NewGuid(),
                    Connection = "default",
                    Queue = parent.Queue,
                    Payload = parent.Payload,
                    ParentJobId = parent.Id,
                    Exception = $"{{\"Message\":\"{reason} (job {job.Id})\"}}",
                    FailedAt = DateTime.UtcNow,
                },
                parent
            );

            logger?.LogWarning(
                "Dead-lettered coordinator {ParentId} on queue {Queue} — its child {JobId} will never complete",
                parent.Id,
                parent.Queue,
                job.Id
            );

            parentId = parent.ParentJobId;
        }
    }

    /// <summary>
    /// Returns a reserved job to the available pool without executing it.
    /// Used by resource-gate logic: when the budget is saturated the worker
    /// releases the reservation and bumps <paramref name="availableAfter"/>
    /// so the job is not immediately picked up again.
    /// </summary>
    /// <remarks>
    /// Do NOT signal <see cref="WorkAvailable"/> here. The job is deferred by
    /// <paramref name="availableAfter"/> — there is no new work for OTHER
    /// workers to wake up for, and the calling worker is about to sleep its
    /// own retry interval. Releasing the semaphore woke every worker on the
    /// shared queue runner, which then burnt through the rest of the queue's
    /// deferred jobs in a tight loop (per-job DB query + JSON deserialize +
    /// budget probe) before any of them landed under the headroom threshold.
    /// </remarks>
    public void ReleaseReservation(QueueJobModel job, TimeSpan availableAfter, int attempt = 0)
    {
        try
        {
            lock (_writeLock)
            {
                job.ReservedAt = null;
                job.AvailableAt = DateTime.UtcNow + availableAfter;
                job.Attempts = (byte)Math.Max(0, job.Attempts - 1);
                context.UpdateJob(job);
                context.SaveChanges();
            }
        }
        catch (Exception e)
        {
            if (e.Source == "Microsoft.EntityFrameworkCore.Relational")
            {
                logger?.LogDebug(
                    e,
                    "Queue DB contention releasing reservation for job {JobId}",
                    job.Id
                );
                return;
            }
            if (attempt < MaxDbRetryAttempts)
            {
                Thread.Sleep(BaseRetryDelayMs + Random.Shared.Next(MaxJitterMs));
                ReleaseReservation(job, availableAfter, attempt + 1);
            }
            else
            {
                logger?.LogError(e, "Failed to release reservation for job {JobId}", job.Id);
            }
        }
    }

    /// <summary>
    /// Replaces the serialized payload of a coordinator job in place and
    /// resets its reservation so it re-enters the queue after
    /// <paramref name="availableAfter"/>. The job's existing ID and queue
    /// slot are preserved — no deduplication check fires.
    /// </summary>
    public void UpdateJobPayload(
        int jobId,
        string newPayload,
        TimeSpan availableAfter,
        int attempt = 0
    )
    {
        try
        {
            lock (_writeLock)
            {
                context.UpdateJobPayload(jobId, newPayload, NextGridWakeUp(availableAfter));
            }

            WorkAvailable.Release();
        }
        catch (Exception e)
        {
            if (e.Source == "Microsoft.EntityFrameworkCore.Relational")
            {
                logger?.LogDebug(e, "Queue DB contention updating payload for job {JobId}", jobId);
                return;
            }
            if (attempt < MaxDbRetryAttempts)
            {
                Thread.Sleep(BaseRetryDelayMs + Random.Shared.Next(MaxJitterMs));
                UpdateJobPayload(jobId, newPayload, availableAfter, attempt + 1);
            }
            else
            {
                logger?.LogError(e, "Failed to update payload for job {JobId}", jobId);
            }
        }
    }

    /// <summary>
    /// Rounds a self-reschedule up to the next shared wall-clock boundary of
    /// <paramref name="interval"/> instead of stamping <c>UtcNow + interval</c>.
    /// A polling coordinator that wakes at whatever moment it last ran drifts
    /// out of phase with every other coordinator on the same queue — each has
    /// its own independent 30s cycle, so at any given reserve instant, whichever
    /// one's private cycle happens to have just ticked over wins, regardless of
    /// which was dispatched first. Anchoring every coordinator to the same grid
    /// (:00, :30, :00, :30, ... from the Unix epoch) makes them become eligible at
    /// the SAME instants, so the reserve query's `ORDER BY CreatedAt` — not poll
    /// timing luck — decides who goes first. Reproduced live: a season dispatched
    /// E02...E88 processed E10 then E11 then skipped back to E02, which had been
    /// eligible-and-waiting the entire time but kept losing the race to jobs
    /// created minutes later whose private cycle happened to align first.
    /// </summary>
    private static DateTime NextGridWakeUp(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
            return DateTime.UtcNow;

        long intervalTicks = interval.Ticks;
        long nowTicks = DateTime.UtcNow.Ticks;
        long nextTick = (nowTicks / intervalTicks + 1) * intervalTicks;
        return new DateTime(nextTick, DateTimeKind.Utc);
    }

    /// <summary>
    /// Moves a reserved job onto a different queue with a replaced payload
    /// and resets its reservation so it re-enters immediately. Used by the
    /// GPU-budget safety net: a job pinned to a GPU device key that will
    /// never be satisfiable (the vendor isn't physically present on this
    /// host) is re-planned onto a software <c>ResourceRequirement</c> and
    /// rerouted to its now-CPU queue instead of looping at the budget gate
    /// forever. Attempts reset to 0 — the degraded requirement is a
    /// different, previously-unattempted shape of the job.
    /// </summary>
    public void Requeue(QueueJobModel job, string newQueue, string newPayload, int attempt = 0)
    {
        try
        {
            lock (_writeLock)
            {
                job.Queue = newQueue;
                job.ReservedAt = null;
                job.AvailableAt = DateTime.UtcNow;
                job.Attempts = 0;
                context.UpdateJob(job);
                context.UpdateJobPayload(job.Id, newPayload, DateTime.UtcNow);
                context.SaveChanges();
            }

            WorkAvailable.Release();
        }
        catch (Exception e)
        {
            if (e.Source == "Microsoft.EntityFrameworkCore.Relational")
            {
                logger?.LogDebug(e, "Queue DB contention requeuing job {JobId}", job.Id);
                return;
            }
            if (attempt < MaxDbRetryAttempts)
            {
                Thread.Sleep(BaseRetryDelayMs + Random.Shared.Next(MaxJitterMs));
                Requeue(job, newQueue, newPayload, attempt + 1);
            }
            else
            {
                logger?.LogError(e, "Failed to requeue job {JobId}", job.Id);
            }
        }
    }

    public void DeleteJob(QueueJobModel queueJob, int attempt = 0)
    {
        try
        {
            lock (_writeLock)
            {
                context.RemoveJob(queueJob);
            }
        }
        catch (Exception e)
        {
            if (e.Source == "Microsoft.EntityFrameworkCore.Relational")
            {
                logger?.LogDebug(e, "Queue DB contention deleting job {JobId}", queueJob.Id);
                return;
            }
            if (attempt < MaxDbRetryAttempts)
            {
                Thread.Sleep(BaseRetryDelayMs + Random.Shared.Next(MaxJitterMs));
                DeleteJob(queueJob, attempt + 1);
                return;
            }

            logger?.LogError(e, "Failed to delete queue job {JobId}", queueJob.Id);
        }
    }

    public void RequeueFailedJob(int failedJobId, int attempt = 0)
    {
        try
        {
            lock (_writeLock)
            {
                FailedJobModel? failedJob = context.FindFailedJob(failedJobId);
                if (failedJob == null)
                    return;

                context.RemoveFailedJob(failedJob);
                context.AddJob(
                    new()
                    {
                        Queue = failedJob.Queue,
                        Payload = failedJob.Payload,
                        AvailableAt = DateTime.UtcNow,
                        Attempts = 0,
                    }
                );

                context.SaveChanges();
            }
        }
        catch (Exception e)
        {
            if (e.Source == "Microsoft.EntityFrameworkCore.Relational")
            {
                logger?.LogDebug(
                    e,
                    "Queue DB contention requeuing failed job {FailedJobId}",
                    failedJobId
                );
                return;
            }
            if (attempt < MaxDbRetryAttempts)
            {
                Thread.Sleep(BaseRetryDelayMs + Random.Shared.Next(MaxJitterMs));
                RequeueFailedJob(failedJobId, attempt + 1);
            }
            else
            {
                logger?.LogError(e, "Failed to requeue failed job {FailedJobId}", failedJobId);
            }
        }
    }

    public void RetryFailedJobs(long? failedJobId = null)
    {
        lock (_writeLock)
        {
            IReadOnlyList<FailedJobModel> failedJobs = context.GetFailedJobs(failedJobId);

            foreach (FailedJobModel failedJob in failedJobs)
            {
                context.AddJob(
                    new()
                    {
                        Queue = failedJob.Queue,
                        Payload = failedJob.Payload,
                        AvailableAt = DateTime.UtcNow,
                        Attempts = 0,
                    }
                );

                context.RemoveFailedJob(failedJob);
            }

            context.SaveChanges();
        }
    }
}
