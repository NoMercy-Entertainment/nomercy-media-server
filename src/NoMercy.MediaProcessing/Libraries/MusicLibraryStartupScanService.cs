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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NoMercy.Database;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.NmSystem.Domain;

namespace NoMercy.MediaProcessing.Libraries;

/// <summary>
/// Scans every music library once after the server has started, so files that another
/// machine moved or removed while this server was down reach the Track rows. The scan
/// is the same one a manual rescan runs; nothing repeats on a timer.
/// </summary>
public sealed class MusicLibraryStartupScanService(
    IHostApplicationLifetime lifetime,
    IDbContextFactory<MediaContext> contextFactory,
    JobDispatcher jobDispatcher,
    ILogger<MusicLibraryStartupScanService> logger
) : BackgroundService
{
    internal static readonly TimeSpan InitialGrace = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await WaitForApplicationStartedAsync(stoppingToken);
            await Task.Delay(InitialGrace, stoppingToken);
            await DispatchScansAsync(stoppingToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Startup scan of the music libraries failed");
        }
    }

    internal async Task DispatchScansAsync(CancellationToken cancellationToken)
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(
            cancellationToken
        );
        List<Ulid> libraryIds = await context
            .Libraries.AsNoTracking()
            .Where(library => library.Type == MediaTypes.MusicMediaType)
            .Select(library => library.Id)
            .ToListAsync(cancellationToken);

        foreach (Ulid libraryId in libraryIds)
            jobDispatcher.DispatchJob<LibraryScanJob>(libraryId);

        logger.LogInformation("Queued a startup scan for {Count} music library(ies)", libraryIds.Count);
    }

    private async Task WaitForApplicationStartedAsync(CancellationToken cancellationToken)
    {
        if (lifetime.ApplicationStarted.IsCancellationRequested)
            return;

        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using CancellationTokenRegistration startedRegistration =
            lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await using CancellationTokenRegistration cancelRegistration = cancellationToken.Register(
            () => started.TrySetCanceled(cancellationToken)
        );
        await started.Task;
    }
}
