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

using NoMercy.Data.Services;

namespace NoMercy.Service.Workers;

/// <summary>
/// On the first start after the update that took music analysis out of the
/// server, removes the folder the derived audio was kept in and says how much
/// room that gave back. Runs beside the start instead of in it, so a folder
/// with many files never delays the server coming up. On every later start
/// the folder is not there and this does nothing.
/// </summary>
public class RetiredDerivedAudioFolderStartupService(
    IRetiredDerivedAudioFolder folder,
    ILogger<RetiredDerivedAudioFolderStartupService> logger
) : BackgroundService
{
    private const long BytesPerMegabyte = 1024 * 1024;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        try
        {
            long? freedBytes = await folder.RemoveAsync(stoppingToken);
            if (freedBytes is not null)
                logger.LogInformation(
                    "Removed the derived-audio folder the server no longer uses; {Megabytes} MB was given back",
                    freedBytes.Value / BytesPerMegabyte
                );
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The server is stopping. The next start finishes the removal.
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to remove the derived-audio folder on startup; it is tried again on the next start"
            );
        }
    }
}
