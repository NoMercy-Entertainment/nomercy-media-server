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

namespace NoMercy.Authorization;

/// <summary>Refreshes the user cache on a context of its own, for callers that hold a factory.</summary>
public static class UserCacheRefresh
{
    public static async Task RefreshUsersAsync(
        this IUserCache cache,
        IDbContextFactory<MediaContext> contextFactory,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        await cache.RefreshUsersAsync(context);
    }

    public static async Task RefreshFolderIdsAsync(
        this IUserCache cache,
        IDbContextFactory<MediaContext> contextFactory,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        await cache.RefreshFolderIdsAsync(context);
    }
}
