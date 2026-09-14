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

using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.Music;
using NoMercy.Database.Models.TvShows;

namespace NoMercy.Data.Repositories;

/// <summary>
/// Media-side lookups behind the dashboard's encoder-queue cards — the movie,
/// episode, track and folder details a queued job's payload only carries an
/// id for.
/// </summary>
public interface IQueueCardMediaRepository
{
    /// <summary>Folders with their linked encoding presets, for the "Profile" field.</summary>
    Task<List<Folder>> GetFoldersWithPresetsAsync(List<Ulid> folderIds);

    Task<List<Movie>> GetMoviesAsync(List<int> movieIds);

    /// <summary>Episodes with their show, needed to build the episode's display title.</summary>
    Task<List<Episode>> GetEpisodesWithShowAsync(List<int> episodeIds);

    /// <summary>Tracks with their album link, needed to build the track's display name.</summary>
    Task<List<Track>> GetTracksWithAlbumAsync(List<Guid> trackIds);

    /// <summary>Video files for a set of host folders, with their episode/movie for maintenance cards.</summary>
    Task<List<VideoFile>> GetVideoFilesByHostFoldersAsync(List<string> hostFolders);

    /// <summary>
    /// Tracks already encoded per release — the real numerator for an album
    /// card's progress, since the metadata-declared total never moves once set.
    /// </summary>
    Task<Dictionary<Guid, int>> GetEncodedTrackCountsByReleaseAsync(List<Guid> releaseIds);

    Task<Dictionary<Guid, string?>> GetAlbumCoversAsync(List<Guid> releaseIds);

    /// <summary>
    /// The same release's cover read off its tracks, for a release whose own
    /// row has not been filled in yet by the cover-art job.
    /// </summary>
    Task<Dictionary<Guid, string?>> GetFallbackTrackCoversAsync(List<Guid> releaseIds);
}
