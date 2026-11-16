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

using NoMercyQueue.Core.Interfaces;
using NoMercyQueue.Core.Models;

namespace NoMercy.Queue.MediaServer;

/// <summary>
/// Outcome of a single <see cref="OrphanRecoveryTriage.RunAsync"/> pass.
/// </summary>
public readonly record struct OrphanTriageResult(int Failed, int Requeued, int Resumable);

/// <summary>
/// The single source of truth for orphan-job triage: given a batch of jobs
/// whose <c>ReservedAt</c> is older than a caller-chosen cutoff, decides for
/// each one whether it is checkpoint-resumable, deserves one more clean retry,
/// or has burned its budget and must dead-letter.
///
/// <para>Both <see cref="OrphanJobRecoveryHostedService"/> (one-shot, on boot)
/// and <see cref="StuckReservationReaperHostedService"/> (periodic, while the
/// server is running) call this same method so the recovery decision never
/// forks into two divergent implementations. The two callers differ in one
/// thing only — <paramref name="refundAttemptOnRequeue"/> — because they face
/// different convergence requirements; see that parameter's doc.</para>
/// </summary>
public static class OrphanRecoveryTriage
{
    public const string InterruptedReason = "job.interrupted_no_checkpoint";

    /// <summary>
    /// How many times a job may lose its worker to the process dying before it
    /// is retired anyway.
    /// <para>Set high on purpose. Restarts are routine — a development session
    /// or an update run can easily interrupt the same long encode several times
    /// — and none of them say anything about the job. This threshold exists for
    /// one case only: a job that crashes the process every time it runs would
    /// otherwise be re-queued by every boot pass forever, and take the server
    /// down with it each time.</para>
    /// </summary>
    public const byte MaxInterruptions = 10;

    /// <summary>
    /// Triages <paramref name="orphans"/> in place and persists the outcome via
    /// <paramref name="context"/>. <paramref name="encoderQueues"/> identifies
    /// which of the orphan's <c>Queue</c> values are encoder queues — only
    /// those consult <paramref name="checkpointLookup"/> for a resumable crash
    /// checkpoint; every other queue follows the plain attempt-budget path.
    /// </summary>
    /// <param name="interrupted">
    /// Whether the reservations being reclaimed were lost to the process going
    /// away rather than to anything the jobs did. True for the boot pass:
    /// nothing was running when the host came up, so every reservation it finds
    /// belongs to a worker that no longer exists — a restart, a kill, a power
    /// cut. Those jobs never got to fail, so the attempt <c>ReserveJob</c>
    /// charged them on reservation is refunded and only
    /// <see cref="QueueJobModel.Interruptions"/> goes up; a job is retired on
    /// that count alone, and only after <see cref="MaxInterruptions"/> of them,
    /// which is the case of a job that takes the process down with it every
    /// time it runs.
    /// <para>False for the periodic reaper. The host is up and the worker is
    /// alive; a reservation older than the cutoff means the JOB is wedged, and
    /// that is a real failure. Attempts stays where it is so the next
    /// reservation's normal increment carries a repeat-hanging job over the
    /// dead-letter threshold within a couple of reclaims — refunding there
    /// would hold it at <c>Attempts == 1</c> forever, re-duplicating its work
    /// every interval without bound.</para>
    /// </param>
    /// <param name="deadLetterReasonFactory">
    /// Produces the <c>FailedJobModel.Exception</c> string for a dead-lettered
    /// orphan. Defaults to <see cref="InterruptedReason"/> (the boot-pass
    /// reason); the periodic reaper supplies a distinct reason that names the
    /// repeated-reclaim cause instead of implying a single crash.
    /// </param>
    /// <param name="onReclaimed">
    /// Invoked once per orphan with the reservation age it had before triage
    /// touched it and a short outcome tag, letting a periodic caller log
    /// per-job detail that a one-shot boot pass doesn't need.
    /// </param>
    public static async Task<OrphanTriageResult> RunAsync(
        IQueueContext context,
        IOrphanCheckpointLookup? checkpointLookup,
        IReadOnlyList<QueueJobModel> orphans,
        IReadOnlySet<string> encoderQueues,
        bool interrupted,
        Func<QueueJobModel, string>? deadLetterReasonFactory = null,
        Action<QueueJobModel, DateTime?, string>? onReclaimed = null,
        CancellationToken cancellationToken = default
    )
    {
        int failed = 0;
        int requeued = 0;
        int resumable = 0;

        foreach (QueueJobModel orphan in orphans)
        {
            DateTime? reservedAt = orphan.ReservedAt;
            bool isEncoderJob = encoderQueues.Contains(orphan.Queue);

            bool hasCheckpoint =
                isEncoderJob
                && checkpointLookup is not null
                && await checkpointLookup
                    .HasCheckpointAsync(orphan.Payload, cancellationToken)
                    .ConfigureAwait(false);

            // An interrupted job is charged an interruption, never an attempt.
            // ReserveJob increments Attempts the moment it hands a job out, so
            // a job whose worker vanished is holding a charge for work it never
            // got to do — and three restarts during one long encode would
            // otherwise retire a perfectly good file with nothing recorded
            // anywhere to say why.
            if (interrupted)
            {
                orphan.Interruptions = (byte)Math.Min(byte.MaxValue, orphan.Interruptions + 1);
                orphan.Attempts = (byte)Math.Max(0, orphan.Attempts - 1);
            }

            // A checkpoint never resets or exempts either budget: a job that hangs
            // or takes the process down on every run leaves a checkpoint behind
            // too, and would otherwise be resumed forever.
            bool exhausted = interrupted
                ? orphan.Interruptions >= MaxInterruptions
                : orphan.Attempts > 1;

            if (exhausted)
            {
                string reason = deadLetterReasonFactory?.Invoke(orphan) ?? InterruptedReason;

                context.AddFailedJobAndRemoveJob(
                    new()
                    {
                        Uuid = Guid.NewGuid(),
                        Connection = "default",
                        Queue = orphan.Queue,
                        Payload = orphan.Payload,
                        Exception = reason,
                        FailedAt = DateTime.UtcNow,
                    },
                    orphan
                );
                failed++;
                onReclaimed?.Invoke(orphan, reservedAt, "failed-exhausted");
            }
            else if (hasCheckpoint)
            {
                orphan.ReservedAt = null;
                context.UpdateJob(orphan);
                resumable++;
                onReclaimed?.Invoke(orphan, reservedAt, "resumed-from-checkpoint");
            }
            else
            {
                orphan.ReservedAt = null;
                context.UpdateJob(orphan);
                requeued++;
                onReclaimed?.Invoke(orphan, reservedAt, "requeued");
            }
        }

        context.SaveChanges();

        return new(failed, requeued, resumable);
    }
}
