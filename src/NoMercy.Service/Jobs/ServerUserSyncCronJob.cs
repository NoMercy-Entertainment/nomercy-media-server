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
using NoMercy.Authorization;
using NoMercy.Database;
using NoMercy.NmSystem.Auth;
using NoMercy.Service.Seeds;
using NoMercy.Storage;
using NoMercyQueue.Core;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.Service.Jobs;

/// <summary>
/// Keeps the local Users table in sync with nomercy-tv's server-users list on an
/// ongoing basis, so a user invited (or removed/declined) after this server's
/// first boot is picked up without a manual restart. <see cref="UsersSeed"/>
/// only ever runs once, on an empty Users table — this job is the only path
/// that re-syncs afterward.
/// </summary>
public class ServerUserSyncCronJob : ICronJobExecutor
{
    private readonly IDbContextFactory<MediaContext> _contextFactory;
    private readonly IServerUserSyncService _syncService;
    private readonly IStorage _storage;
    private readonly IAuthTokenStore _authTokenStore;
    private readonly IUserCache _userCache;
    private readonly ILogger<ServerUserSyncCronJob> _logger;

    public string CronExpression => new CronExpressionBuilder().EveryMinutes(5);
    public string JobName => "Server User Sync";

    public ServerUserSyncCronJob(
        IDbContextFactory<MediaContext> contextFactory,
        IServerUserSyncService syncService,
        IStorage storage,
        IAuthTokenStore authTokenStore,
        IUserCache userCache,
        ILogger<ServerUserSyncCronJob> logger
    )
    {
        _contextFactory = contextFactory;
        _syncService = syncService;
        _storage = storage;
        _authTokenStore = authTokenStore;
        _userCache = userCache;
        _logger = logger;
    }

    public async Task ExecuteAsync(string parameters, CancellationToken cancellationToken = default)
    {
        string? token = _authTokenStore.AccessToken;
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogDebug("Server user sync skipped — no auth token yet");
            return;
        }

        await using MediaContext context = await _contextFactory.CreateDbContextAsync(
            cancellationToken
        );

        ServerUserSyncResult result = await _syncService.SyncAsync(
            context,
            _storage,
            token,
            cancellationToken
        );

        if (!result.Attempted)
            return;

        // The allow-list the "api" auth policy checks is the in-memory UserCache,
        // not the table directly — refresh it so an invite accepted this run
        // grants access immediately instead of waiting for whatever unrelated
        // event happens to refresh the cache next.
        await _userCache.RefreshUsersAsync(_contextFactory, cancellationToken);

        // A no-op sync (nobody revoked) is routine background chatter — keep it at
        // Debug. A run that actually revoked access is a real event worth Info.
        LogLevel syncLevel = result.RevokedCount > 0 ? LogLevel.Information : LogLevel.Debug;
        _logger.Log(
            syncLevel,
            "Server user sync complete: {Count} upstream user(s), {Revoked} revoked locally",
            [result.UpstreamUserCount, result.RevokedCount]
        );
    }
}
