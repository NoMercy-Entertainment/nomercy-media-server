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
using NoMercy.Database.Models.Users;

namespace NoMercy.Authorization;

/// <summary>
/// In-memory cache of the server's users and library folder ids, refreshed from
/// the database. Replaces the static mutable state that previously lived on
/// ClaimsPrincipalExtensions.
/// </summary>
public interface IUserCache
{
    IReadOnlyList<User> Users { get; }
    IReadOnlyList<Ulid> FolderIds { get; }

    User? GetUser(Guid userId);

    /// <summary>
    /// Whether <paramref name="userId"/> holds a library grant that covers
    /// <paramref name="folderId"/>: the same <c>LibraryUsers</c> scope every
    /// browse repository applies, so serving a file never widens what a user can list.
    /// </summary>
    bool UserMayAccessFolder(Guid userId, Ulid folderId);

    void AddUser(User user);
    void RemoveUser(User user);
    void UpdateUser(User user);
    void Reset();

    Task InitializeAsync(MediaContext context);
    Task RefreshUsersAsync(MediaContext context);
    Task RefreshFolderIdsAsync(MediaContext context);
}
