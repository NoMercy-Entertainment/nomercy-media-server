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
using NoMercy.Database;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Storage;
using Serilog.Events;

namespace NoMercy.Service.Seeds;

public static class UsersSeed
{
    /// <summary>
    /// First-boot seed only: populates an empty Users table from the current
    /// server-users list. Ongoing reconciliation (invites accepted after this
    /// server already has users, revocation of removed/declined users) is
    /// nomercy-tv's PushServerUserJob, notifying this server directly as each
    /// grant changes — this seed intentionally never re-runs once any user
    /// exists locally.
    /// </summary>
    public static async Task Init(
        this MediaContext dbContext,
        IStorage storage,
        string? accessToken,
        IServerUserSyncService? syncService = null
    )
    {
        try
        {
            bool hasUsers = await dbContext.Users.AnyAsync();
            if (hasUsers)
                return;

            Logger.Setup("Adding Users", LogEventLevel.Verbose);

            IServerUserSyncService service =
                syncService ?? new ServerUserSyncService(new ServerUserApiClient());

            await service.SyncAsync(dbContext, storage, accessToken);
        }
        catch (Exception e)
        {
            Logger.Setup($"Users seed failed: {e.Message}", LogEventLevel.Warning);
        }
    }
}
