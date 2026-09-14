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

using NoMercy.Database.Models.Users;

namespace NoMercy.Data.Repositories;

public interface IUserDataRepository
{
    Task<List<UserData>> GetUserDataAsync(
        Guid userId,
        string type,
        int? intId,
        Ulid? ulidId,
        CancellationToken ct = default
    );

    Task<UserData?> GetUserDataSingleAsync(
        Guid userId,
        string type,
        int? intId,
        Ulid? ulidId,
        CancellationToken ct = default
    );

    Task<int> DeleteUserDataAsync(List<UserData> userData, CancellationToken ct = default);

    Task<int> HideFromContinueWatchingAsync(
        IEnumerable<UserData> userData,
        CancellationToken ct = default
    );

    Task<int> RemoveForItemAsync(
        Guid userId,
        string type,
        int? intId,
        Ulid? ulidId,
        CancellationToken ct = default
    );

    /// <summary>
    /// Stores the time watched for a video file, keyed per playlist type. Returns
    /// false, storing nothing, when the type is not a video type or a referenced row
    /// does not exist, such as a file a rescan re-indexed mid-playback.
    /// </summary>
    Task<bool> UpsertWatchProgressAsync(WatchProgress progress, CancellationToken ct = default);

    /// <summary>
    /// Stores the track choice for the title being played, keyed per playlist type:
    /// movie, show (anime included), collection or special.
    /// </summary>
    Task SavePlaybackPreferenceAsync(PlaybackPreference preference, string playlistType);

    /// <summary>
    /// The user with their playback preferences and, per preference, the library and its
    /// movie and show links; null for an unknown user.
    /// </summary>
    Task<User?> GetWithPlaybackPreferencesAsync(Guid userId);

    /// <summary>
    /// Stores <paramref name="preference"/> as the user's default for libraries of
    /// <paramref name="libraryType"/>, unless the user already has one.
    /// </summary>
    Task SaveLibraryPreferenceIfMissingAsync(PlaybackPreference preference, string? libraryType);
}
