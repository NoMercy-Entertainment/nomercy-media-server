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

/*
 * Copyright (c) NoMercy Entertainment. All Rights Reserved.
 */

using NoMercy.Database.Models.Users;

namespace NoMercy.Data.Repositories;

public interface IUserRepository
{
    Task<List<User>> GetAllWithLibrariesAsync();

    Task<User?> GetByIdAsync(Guid userId);

    Task<User?> GetByIdWithLibrariesAsync(Guid userId);

    Task<User?> GetByIdWithNotificationsAsync(Guid userId);

    Task<bool> ExistsAsync(Guid userId);

    Task AddAsync(User user);

    Task<User?> GetByIdWithLibrariesAfterAddAsync(Guid userId);

    Task DeleteAsync(Guid userId);

    Task UpdatePermissionsAsync(
        Guid targetUserId,
        Guid actingUserId,
        bool allowed,
        bool opticalAccess,
        bool audioTranscoding,
        bool videoTranscoding,
        bool noTranscoding,
        bool? manage,
        IEnumerable<Ulid> libraryIds
    );

    /// <summary>Users who may use the server: allowed users and owners.</summary>
    Task<int> CountAllowedAsync(CancellationToken ct = default);
}
