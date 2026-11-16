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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NoMercyQueue.Core;
using NoMercyQueue.Core.Interfaces;
using NoMercyQueue.Core.Models;

namespace NoMercy.Queue.MediaServer;

/// <summary>
/// Phase 4.14 — startup orphan-job recovery. On boot, scans the queue for
/// jobs left in "running" state (<c>ReservedAt != null</c>) by a previous
/// shutdown that are older than the cutoff window.
///
/// Checkpoint-aware triage (Phase 4.14b): encoder-queue orphans that have a
/// crash checkpoint are re-queued and counted as resumable so the resume path
/// gets a chance to pick up from the last-known keyframe position; the
/// checkpoint resets or exempts no budget. Orphans with no
/// checkpoint follow the original logic: a genuine first-time orphan (reserved
/// exactly once — <c>Attempts &lt;= 1</c>, since <c>ReserveJob</c> increments
/// Attempts in the same write that sets ReservedAt) gets one clean retry with
/// its reservation released and its attempt budget refunded; an orphan that
/// has already burned a prior attempt beyond that single reservation
/// (<c>Attempts &gt; 1</c>) is a repeat offender and moves to FailedJobs so it
/// can't retry forever.
///
/// This is a one-shot pass: nothing runs yet when the host boots, so a 30s
/// wall-clock cutoff applied to every queue (encoder included) is safe here.
/// Once the server is up, reclaiming a stuck reservation is no longer
/// unconditionally safe for long-running jobs — see
/// <see cref="StuckReservationReaperHostedService"/>, which runs the same
/// <see cref="OrphanRecoveryTriage"/> logic periodically but only against an
/// allow-list of queues known to finish in seconds-to-minutes (encoder AND
/// library/import are excluded — see that class for why).
/// </summary>
public class OrphanJobRecoveryHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<OrphanJobRecoveryHostedService> logger
) : BackgroundService
{
    private static readonly TimeSpan OrphanCutoff = TimeSpan.FromSeconds(30);
    internal static readonly string[] EncoderQueues =
    [
        QueueNames.Encoder,
        QueueNames.EncoderTask,
        QueueNames.EncoderGpu,
        QueueNames.EncoderCpu,
    ];

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // Run off the startup path so a slow/large recovery sweep never blocks
        // the host from coming up.
        await Task.Yield();

        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            IQueueContext context = scope.ServiceProvider.GetRequiredService<IQueueContext>();
            IOrphanCheckpointLookup? checkpointLookup =
                scope.ServiceProvider.GetService<IOrphanCheckpointLookup>();

            DateTime cutoff = DateTime.UtcNow.Subtract(OrphanCutoff);
            IReadOnlyList<QueueJobModel> orphans = context.GetReservedJobsOlderThan(cutoff);

            if (orphans.Count == 0)
            {
                logger.LogDebug("Orphan recovery: no orphan jobs found");
                return;
            }

            OrphanTriageResult result = await OrphanRecoveryTriage
                .RunAsync(
                    context,
                    checkpointLookup,
                    orphans,
                    EncoderQueues.ToHashSet(),
                    // Nothing was running when the host came up, so every
                    // reservation found here belongs to a worker that no longer
                    // exists. That is the process dying, not the job failing:
                    // the attempt goes back and only the interruption count
                    // moves.
                    interrupted: true,
                    deadLetterReasonFactory: null,
                    onReclaimed: null,
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);

            logger.LogInformation(
                "Orphan recovery: scanned {Total} orphan job(s); {Failed} moved to FailedJobs ({Reason}); {Requeued} left for retry; {Resumable} re-queued for checkpoint resume",
                orphans.Count,
                result.Failed,
                OrphanRecoveryTriage.InterruptedReason,
                result.Requeued,
                result.Resumable
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Orphan recovery failed; continuing startup");
        }

        return;
    }
}
