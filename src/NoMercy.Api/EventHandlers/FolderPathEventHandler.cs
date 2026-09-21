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
using Microsoft.Extensions.DependencyInjection;
using NoMercy.Api.Middleware;
using NoMercy.Authorization;
using NoMercy.Database;
using NoMercy.Events;
using NoMercy.Events.Library;
using NoMercy.Events.Users;

namespace NoMercy.Api.EventHandlers;

/// <summary>
/// Keeps the served-folder registry and the user cache's folder view current:
/// which folders exist, and which users hold a library grant covering each one.
/// </summary>
public class FolderPathEventHandler : EventSubscriber
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IUserCache _userCache;
    private readonly IServedFolderRegistry _servedFolders;

    public FolderPathEventHandler(
        IEventBus eventBus,
        IServiceScopeFactory scopeFactory,
        IUserCache userCache,
        IServedFolderRegistry servedFolders
    )
    {
        _servedFolders = servedFolders;
        _scopeFactory = scopeFactory;
        _userCache = userCache;
        Track(eventBus.Subscribe<FolderPathAddedEvent>(OnFolderPathAdded));
        Track(eventBus.Subscribe<FolderPathRemovedEvent>(OnFolderPathRemoved));
        Track(eventBus.Subscribe<UserPermissionsChangedEvent>(OnUserPermissionsChanged));
    }

    internal async Task OnFolderPathAdded(FolderPathAddedEvent @event, CancellationToken ct)
    {
        _servedFolders.Add(@event.RequestPath, @event.DriverId, @event.SubPath);
        await RefreshFolderCacheAsync(ct);
    }

    internal async Task OnFolderPathRemoved(FolderPathRemovedEvent @event, CancellationToken ct)
    {
        _servedFolders.Remove(@event.RequestPath);
        await RefreshFolderCacheAsync(ct);
    }

    // A library grant change decides which folders a user may be served from.
    internal Task OnUserPermissionsChanged(
        UserPermissionsChangedEvent @event,
        CancellationToken ct
    ) => RefreshFolderCacheAsync(ct);

    private async Task RefreshFolderCacheAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IDbContextFactory<MediaContext> contextFactory = scope.ServiceProvider.GetRequiredService<
            IDbContextFactory<MediaContext>
        >();
        await _userCache.RefreshFolderIdsAsync(contextFactory, ct);
    }
}
