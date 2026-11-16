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

using NoMercy.Database;
using NoMercy.NmSystem.Information;

namespace NoMercy.Service.Workers;

// Keeps the media and queue WAL files bounded while a long import and constant client reads overlap.
public sealed class WalCheckpointService(
    SqliteWalCheckpointer checkpointer,
    ILogger<WalCheckpointService> logger
) : BackgroundService
{
    private const long WalLimitBytes = 64L * 1024 * 1024;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(CheckInterval, stoppingToken);

                await CheckpointAsync(AppFiles.MediaDatabase, stoppingToken);
                await CheckpointAsync(AppFiles.QueueDatabase, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down — exit cleanly.
        }
    }

    private async Task CheckpointAsync(string databasePath, CancellationToken stoppingToken)
    {
        try
        {
            WalCheckpointResult result = await checkpointer.TruncateIfOverLimitAsync(
                databasePath,
                WalLimitBytes,
                stoppingToken
            );

            if (result.Truncated)
                logger.LogInformation(
                    "WAL of {Database} restarted at {WalFrames} frames",
                    Path.GetFileName(databasePath),
                    result.WalFrames
                );
            else if (result.OverLimit)
                logger.LogWarning(
                    "WAL of {Database} holds {WalFrames} frames and a running read blocked its restart",
                    Path.GetFileName(databasePath),
                    result.WalFrames
                );
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(
                exception,
                "WAL checkpoint of {Database} failed",
                Path.GetFileName(databasePath)
            );
        }
    }
}
