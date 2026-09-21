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
using NoMercy.Database.Models.Users;

namespace NoMercy.Authorization;

public class UserCache : IUserCache
{
    private readonly Lock _usersLock = new();
    private readonly Lock _folderIdsLock = new();

    private List<User> _users = [];
    private List<Ulid> _folderIds = [];
    private Dictionary<Ulid, HashSet<Guid>> _folderUsers = [];

    public IReadOnlyList<User> Users
    {
        get
        {
            lock (_usersLock)
                return [.. _users];
        }
    }

    public IReadOnlyList<Ulid> FolderIds
    {
        get
        {
            lock (_folderIdsLock)
                return [.. _folderIds];
        }
    }

    public User? GetUser(Guid userId)
    {
        lock (_usersLock)
            return _users.FirstOrDefault(u => u.Id == userId);
    }

    public bool UserMayAccessFolder(Guid userId, Ulid folderId)
    {
        lock (_folderIdsLock)
            return _folderUsers.TryGetValue(folderId, out HashSet<Guid>? users)
                && users.Contains(userId);
    }

    public void AddUser(User user)
    {
        lock (_usersLock)
            _users = [.. _users, user];
    }

    public void RemoveUser(User user)
    {
        lock (_usersLock)
            _users = _users.Where(u => u.Id != user.Id).ToList();
    }

    public void UpdateUser(User user)
    {
        lock (_usersLock)
            _users = _users.Select(u => u.Id == user.Id ? user : u).ToList();
    }

    public void Reset()
    {
        lock (_usersLock)
            _users = [];

        lock (_folderIdsLock)
        {
            _folderIds = [];
            _folderUsers = [];
        }
    }

    public async Task InitializeAsync(MediaContext context)
    {
        List<User> users = await context.Users.AsNoTracking().ToListAsync();

        lock (_usersLock)
            _users = users;

        await RefreshFolderIdsAsync(context);
    }

    public async Task RefreshUsersAsync(MediaContext context)
    {
        List<User> users = await context.Users.AsNoTracking().ToListAsync();

        lock (_usersLock)
            _users = users;
    }

    public async Task RefreshFolderIdsAsync(MediaContext context)
    {
        List<Ulid> folderIds = await context.Folders.AsNoTracking().Select(x => x.Id).ToListAsync();

        List<FolderGrant> grants = await context
            .FolderLibrary.AsNoTracking()
            .Join(
                context.LibraryUser.AsNoTracking(),
                folderLibrary => folderLibrary.LibraryId,
                libraryUser => libraryUser.LibraryId,
                (folderLibrary, libraryUser) =>
                    new FolderGrant(folderLibrary.FolderId, libraryUser.UserId)
            )
            .ToListAsync();

        Dictionary<Ulid, HashSet<Guid>> folderUsers = grants
            .GroupBy(grant => grant.FolderId)
            .ToDictionary(group => group.Key, group => group.Select(g => g.UserId).ToHashSet());

        lock (_folderIdsLock)
        {
            _folderIds = folderIds;
            _folderUsers = folderUsers;
        }
    }

    private sealed record FolderGrant(Ulid FolderId, Guid UserId);

    // Ambient instance bridging the static ClaimsPrincipalExtensions delegators
    // to the same state as the DI-registered singleton during migration.
    public static UserCache Current { get; } = new();
}
