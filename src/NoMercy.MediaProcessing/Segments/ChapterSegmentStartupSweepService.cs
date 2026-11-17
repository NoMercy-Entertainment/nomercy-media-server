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

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NoMercy.NmSystem.Monitoring;

namespace NoMercy.MediaProcessing.Segments;

/// <summary>
/// Runs the chapter segment sweep once after boot, so existing libraries get
/// their chapter segments without waiting for the daily 05:30 UTC run.
/// <para>
/// It never blocks boot: it waits in the background for the host to report
/// started (as <c>LibraryStartupScanService</c> does), then a grace period so
/// startup work goes first, then for playback to go idle. The sweep itself is
/// database-only and idempotent, so racing the daily run is harmless.
/// </para>
/// </summary>
public sealed class ChapterSegmentStartupSweepService(
    IHostApplicationLifetime lifetime,
    IChapterSegmentWriter writer,
    MediaActivityMonitor activityMonitor,
    ILogger<ChapterSegmentStartupSweepService> logger,
    TimeSpan? initialGrace = null
) : BackgroundService
{
    internal static readonly TimeSpan DefaultInitialGrace = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaxPlaybackWait = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await WaitForApplicationStartedAsync(stoppingToken);
            await Task.Delay(initialGrace ?? DefaultInitialGrace, stoppingToken);
            await SweepAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Startup chapter segment sweep failed; the daily sweep retries");
        }
    }

    internal async Task SweepAsync(CancellationToken ct)
    {
        await activityMonitor.WaitForIdleAsync(MaxPlaybackWait, ct);
        int changed = await writer.ClassifyAllAsync(ct);

        logger.LogInformation(
            "Startup chapter segment sweep: segments changed for {Count} episode(s) or movie(s)",
            changed
        );
    }

    private async Task WaitForApplicationStartedAsync(CancellationToken ct)
    {
        if (lifetime.ApplicationStarted.IsCancellationRequested)
            return;

        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using CancellationTokenRegistration startedRegistration =
            lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await using CancellationTokenRegistration cancelRegistration = ct.Register(() =>
            started.TrySetCanceled(ct)
        );
        await started.Task;
    }
}
