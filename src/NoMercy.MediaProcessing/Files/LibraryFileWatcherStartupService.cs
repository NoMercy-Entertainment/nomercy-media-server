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

namespace NoMercy.MediaProcessing.Files;

/// <summary>
/// Starts the file watchers of every library, music and video, once the host has started.
/// Every file action then reaches the file event handlers.
/// </summary>
public sealed class LibraryFileWatcherStartupService(
    IHostApplicationLifetime lifetime,
    ILibraryFileWatcherStarter starter,
    ILogger<LibraryFileWatcherStartupService> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await WaitForApplicationStartedAsync(stoppingToken);
            // The watcher reads the library folders and blocks while it arms them.
            await Task.Run(starter.Start, stoppingToken);
            logger.LogInformation("File watchers started for every library");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Starting the library file watchers failed");
        }
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
