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

using Microsoft.EntityFrameworkCore.Storage;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.NmSystem.Dto;

namespace NoMercy.MediaProcessing.Files;

public interface IFileRepository
{
    Task<IDbContextTransaction> BeginTransactionAsync();
    Task StoreVideoFile(VideoFile videoFile);
    Task<Ulid> StoreMetadata(Metadata metadata);
    Task<Episode?> GetEpisode(int? showId, MediaFile item);

    /// <summary>
    /// Looks up an episode by its known id — used for the file a post-encode scan
    /// was dispatched for, instead of re-deriving the episode from the filename.
    /// </summary>
    Task<Episode?> GetEpisodeById(int episodeId);

    /// <summary>
    /// Records that a video file in a TV library resolved to no episode. Such a file is
    /// stored with no episode and no movie, so nothing can ever list or play it, and the
    /// library filter (which requires an episode with a playable file) hides the whole
    /// show — leaving a library that looks empty for no stated reason.
    /// </summary>
    Task RecordUnmatchedEpisodeFileAsync(string filePath, Ulid libraryId, string reason);
    Task<(Movie? movie, Tv? show, string type)> MediaType(int id, Library library);
    Task<int> DeleteVideoFilesByHostFolderAsync(string hostFolder);
    Task<int> DeleteMetadataByHostFolderAsync(string hostFolder);
    Task<int> UpdateVideoFilePathsAsync(
        string oldHostFolder,
        string oldFilename,
        string newHostFolder,
        string newFilename
    );
    Task<int> UpdateVideoFileSubtitlesAsync(
        Ulid videoFileId,
        string subtitlesJson,
        CancellationToken ct = default
    );

    /// <summary>
    /// Points a folder's registered preview tracks at the sprite sheet that is on
    /// disk now.
    ///
    /// <para>A scan registers the pair it finds and, in the same pass, queues the
    /// upgrade that replaces it. The upgrade deletes what it superseded, so the
    /// registration went on naming a file the server itself had removed and every
    /// client asked for it — a 404 on every scrub, on exactly the titles the
    /// upgrade was meant to improve. Whoever rewrites the sidecar owns the
    /// registration that names it.</para>
    /// </summary>
    /// <returns>How many video files were repointed.</returns>
    Task<int> RepointPreviewTracksAsync(
        string hostFolder,
        string sheetFileName,
        string vttFileName,
        CancellationToken ct = default
    );
    Task DeleteVideoFilesAndMetadataByMovieIdAsync(int movieId);
    Task DeleteVideoFilesAndMetadataByTvIdAsync(int tvId);

    /// <summary>
    /// Deletes only the VideoFiles (and their Metadata) rows that are safe to
    /// reconcile after a rescan: rows whose Share is in
    /// <paramref name="eligibleShares"/> (the folders this pass actually
    /// re-scanned) AND whose (Share, HostFolder, Filename) is NOT in
    /// <paramref name="storedKeys"/> (what this pass re-stored). A Share
    /// absent from <paramref name="eligibleShares"/> — its folder failed to
    /// enumerate or one of its items was skipped — keeps every one of its
    /// existing rows untouched, rather than wiping a show that is still
    /// fully on disk because one unrelated folder had a problem.
    /// </summary>
    Task DeleteStaleVideoFilesAndMetadataByMovieIdAsync(
        int movieId,
        List<string> eligibleShares,
        HashSet<RecordedVideoFileLocation> storedKeys
    );

    /// <inheritdoc cref="DeleteStaleVideoFilesAndMetadataByMovieIdAsync"/>
    Task DeleteStaleVideoFilesAndMetadataByTvIdAsync(
        int tvId,
        List<string> eligibleShares,
        HashSet<RecordedVideoFileLocation> storedKeys
    );

    /// <summary>
    /// The paths already registered for a title. A rescan that resolves nothing has
    /// to prove the media is gone before it deletes anything, and these rows are the
    /// only record of where it was.
    /// </summary>
    Task<List<RecordedVideoFileLocation>> GetRecordedVideoFileLocationsByMovieIdAsync(int movieId);

    /// <inheritdoc cref="GetRecordedVideoFileLocationsByMovieIdAsync"/>
    Task<List<RecordedVideoFileLocation>> GetRecordedVideoFileLocationsByTvIdAsync(int tvId);

    Task<List<VideoFile>> SearchVideoFilesAsync(
        string? query,
        int limit,
        CancellationToken ct = default
    );
}
