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
using Microsoft.EntityFrameworkCore.Storage;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Users;

namespace NoMercy.Data.Repositories;

public class UserRepository(MediaContext context, IDbContextFactory<MediaContext> contextFactory)
    : IUserRepository
{
    public Task<List<User>> GetAllWithLibrariesAsync()
    {
        return context
            .Users.AsNoTracking()
            .Include(user => user.LibraryUser)
                .ThenInclude(libraryUser => libraryUser.Library)
            .ToListAsync();
    }

    public Task<User?> GetByIdAsync(Guid userId)
    {
        return context.Users.AsNoTracking().FirstOrDefaultAsync(user => user.Id == userId);
    }

    public Task<User?> GetByIdWithLibrariesAsync(Guid userId)
    {
        return context
            .Users.AsNoTracking()
            .Include(user => user.LibraryUser)
                .ThenInclude(libraryUser => libraryUser.Library)
            .FirstOrDefaultAsync(user => user.Id == userId);
    }

    public Task<User?> GetByIdWithNotificationsAsync(Guid userId)
    {
        return context
            .Users.AsNoTracking()
            .Where(user => user.Id == userId)
            .Include(user => user.LibraryUser)
            .Include(user => user.NotificationUser)
                .ThenInclude(notificationUser => notificationUser.Notification)
            .FirstOrDefaultAsync(user => user.Id == userId);
    }

    public Task<bool> ExistsAsync(Guid userId)
    {
        return context.Users.AnyAsync(user => user.Id == userId);
    }

    public async Task AddAsync(User user)
    {
        context.Users.Add(user);
        await context.SaveChangesAsync();
    }

    public Task<User?> GetByIdWithLibrariesAfterAddAsync(Guid userId)
    {
        return context
            .Users.Include(user => user.LibraryUser)
            .FirstOrDefaultAsync(user => user.Id == userId);
    }

    public async Task DeleteAsync(Guid userId)
    {
        await using MediaContext deleteContext = await contextFactory.CreateDbContextAsync();

        bool exists = await deleteContext.Users.AnyAsync(user => user.Id == userId);
        if (!exists)
            return;

        // Every table that references User defaults to DeleteBehavior.Restrict
        // (MediaContext's schema-wide default), so deleting the User row while any of them
        // still reference it throws instead of deleting. Clear each Restrict dependent
        // first, deepest first, then delete the User; the database itself then resolves the
        // remaining Cascade/SetNull foreign keys (Device un-owns itself, PlaylistItem
        // cascades from its UserPlaylist). Dropping the account legitimately drops that
        // user's own grants, preferences and history and touches no other user's data.
        // The whole thing runs in one transaction so a mid-sweep failure never leaves a
        // half-deleted account behind.
        await using IDbContextTransaction transaction =
            await deleteContext.Database.BeginTransactionAsync();

        // PlaylistTrack -> Playlist is itself Restrict, so it has to go before the user's
        // Playlist rows can be removed.
        await deleteContext
            .PlaylistTrack.Where(pt =>
                deleteContext.Playlists.Any(p => p.Id == pt.PlaylistId && p.UserId == userId)
            )
            .ExecuteDeleteAsync();

        await deleteContext.LibraryUser.Where(lu => lu.UserId == userId).ExecuteDeleteAsync();
        await deleteContext.MovieUser.Where(mu => mu.UserId == userId).ExecuteDeleteAsync();
        await deleteContext.TvUser.Where(tu => tu.UserId == userId).ExecuteDeleteAsync();
        await deleteContext.CollectionUser.Where(cu => cu.UserId == userId).ExecuteDeleteAsync();
        await deleteContext.SpecialUser.Where(su => su.UserId == userId).ExecuteDeleteAsync();
        await deleteContext.NotificationUser.Where(nu => nu.UserId == userId).ExecuteDeleteAsync();
        await deleteContext.AlbumUser.Where(au => au.UserId == userId).ExecuteDeleteAsync();
        await deleteContext.ArtistUser.Where(au => au.UserId == userId).ExecuteDeleteAsync();
        await deleteContext.TrackUser.Where(tu => tu.UserId == userId).ExecuteDeleteAsync();
        await deleteContext
            .PlaybackPreferences.Where(pp => pp.UserId == userId)
            .ExecuteDeleteAsync();
        await deleteContext.MusicPlays.Where(mp => mp.UserId == userId).ExecuteDeleteAsync();
        await deleteContext.UserData.Where(ud => ud.UserId == userId).ExecuteDeleteAsync();
        await deleteContext.ActivityLogs.Where(al => al.UserId == userId).ExecuteDeleteAsync();
        await deleteContext.DeviceDropNotices.Where(dn => dn.UserId == userId).ExecuteDeleteAsync();
        await deleteContext.UserPlaylists.Where(up => up.UserId == userId).ExecuteDeleteAsync();
        await deleteContext.Playlists.Where(p => p.UserId == userId).ExecuteDeleteAsync();

        await deleteContext.Users.Where(user => user.Id == userId).ExecuteDeleteAsync();

        await transaction.CommitAsync();
    }

    public async Task UpdatePermissionsAsync(
        Guid targetUserId,
        Guid actingUserId,
        bool allowed,
        bool opticalAccess,
        bool audioTranscoding,
        bool videoTranscoding,
        bool noTranscoding,
        bool? manage,
        IEnumerable<Ulid> libraryIds
    )
    {
        await using MediaContext permContext = await contextFactory.CreateDbContextAsync();

        User? user = await permContext
            .Users.Include(user => user.LibraryUser)
            .FirstOrDefaultAsync(user => user.Id == targetUserId);

        if (user is null)
            return;

        if (manage.HasValue)
            user.Manage = manage.Value;

        user.Allowed = allowed;
        user.OpticalAccess = opticalAccess;
        user.AudioTranscoding = audioTranscoding;
        user.VideoTranscoding = videoTranscoding;
        user.NoTranscoding = noTranscoding;

        user.LibraryUser.Clear();

        foreach (Ulid libraryId in libraryIds)
            user.LibraryUser.Add(new() { LibraryId = libraryId, UserId = targetUserId });

        await permContext.SaveChangesAsync();
    }

    public Task<int> CountAllowedAsync(CancellationToken ct = default)
    {
        return context.Users.AsNoTracking().CountAsync(user => user.Allowed || user.Owner, ct);
    }
}
