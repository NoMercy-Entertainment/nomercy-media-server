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
/// On boot, rewrites the tracks an older importer run stored with their album
/// folder twice inside <c>HostFolder</c>. Those rows address no file at all, so
/// they are unplayable and their analysis fails on every sweep until the value
/// is put right, and their analysis verdict is reset with them so the next sweep
/// picks them up. Runs on every start rather than behind a one-shot flag: on a
/// healthy database it is one query, one table scan and no disk access at all,
/// and it stays a safety net if the shape ever reappears.
/// </summary>
public class DoubledHostFolderRepairStartupService(
    IDoubledHostFolderRepair repair,
    ILogger<DoubledHostFolderRepairStartupService> logger
) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            int repaired = await repair.RunAsync(cancellationToken);
            if (repaired > 0)
                logger.LogInformation(
                    "Repaired {Count} tracks whose host folder held the same folder twice",
                    repaired
                );
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to sweep doubled track host folders on startup; continuing"
            );
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
