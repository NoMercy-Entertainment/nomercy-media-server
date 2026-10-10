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
using NoMercy.Data.Jobs;
using NoMercy.Database;
using NoMercyQueue;

namespace NoMercy.Service.Workers;

/// <summary>
/// Once per install, queues a <see cref="MusicCoverRepairJob"/> that re-fetches
/// the art for covers whose file was never stored. Not repeated on every boot:
/// an artist FanArt has nothing for would be asked about again each time.
/// </summary>
public class MusicCoverRepairStartupService(ILogger<MusicCoverRepairStartupService> logger)
    : IHostedService
{
    private const string DoneKey = "music_cover_repair_done";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (QueueRunner.Current is null)
                return;

            await using AppDbContext db = new();
            Database.Models.Common.Configuration? flag = await db.Configuration.FirstOrDefaultAsync(
                c => c.Key == DoneKey,
                cancellationToken
            );
            if (flag?.Value == "true")
                return;

            QueueRunner.Current.Dispatcher.Dispatch(new MusicCoverRepairJob());

            if (flag is null)
                db.Configuration.Add(new() { Key = DoneKey, Value = "true" });
            else
                flag.Value = "true";
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Music cover repair job dispatched on startup");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch music cover repair on startup; continuing");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
