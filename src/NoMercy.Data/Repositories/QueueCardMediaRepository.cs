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
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.Music;
using NoMercy.Database.Models.TvShows;

namespace NoMercy.Data.Repositories;

/// <inheritdoc cref="IQueueCardMediaRepository"/>
public class QueueCardMediaRepository(MediaContext context) : IQueueCardMediaRepository
{
    public Task<List<Folder>> GetFoldersWithPresetsAsync(List<Ulid> folderIds)
    {
        return context
            .Folders.AsNoTracking()
            .Where(folder => folderIds.Contains(folder.Id))
            .Include(folder => folder.EncodingPresetFolders)
                .ThenInclude(link => link.Preset)
            .ToListAsync();
    }

    public Task<List<Movie>> GetMoviesAsync(List<int> movieIds)
    {
        return context
            .Movies.AsNoTracking()
            .Where(movie => movieIds.Contains(movie.Id))
            .ToListAsync();
    }

    public Task<List<Episode>> GetEpisodesWithShowAsync(List<int> episodeIds)
    {
        return context
            .Episodes.AsNoTracking()
            .Where(episode => episodeIds.Contains(episode.Id))
            .Include(episode => episode.Tv)
            .ToListAsync();
    }

    public Task<List<Track>> GetTracksWithAlbumAsync(List<Guid> trackIds)
    {
        return context
            .Tracks.AsNoTracking()
            .Where(track => trackIds.Contains(track.Id))
            .Include(track => track.AlbumTrack)
                .ThenInclude(albumTrack => albumTrack.Album)
            .ToListAsync();
    }

    public Task<List<VideoFile>> GetVideoFilesByHostFoldersAsync(List<string> hostFolders)
    {
        return context
            .VideoFiles.AsNoTracking()
            .Where(file => hostFolders.Contains(file.HostFolder))
            .Include(file => file.Episode)
                .ThenInclude(episode => episode!.Tv)
            .Include(file => file.Movie)
            .ToListAsync();
    }

    public Task<Dictionary<Guid, int>> GetEncodedTrackCountsByReleaseAsync(List<Guid> releaseIds)
    {
        return context
            .AlbumTrack.AsNoTracking()
            .Where(link => releaseIds.Contains(link.AlbumId))
            .GroupBy(link => link.AlbumId)
            .ToDictionaryAsync(group => group.Key, group => group.Count());
    }

    public Task<Dictionary<Guid, string?>> GetAlbumCoversAsync(List<Guid> releaseIds)
    {
        return context
            .Albums.AsNoTracking()
            .Where(album => releaseIds.Contains(album.Id))
            .ToDictionaryAsync(album => album.Id, album => album.Cover);
    }

    public async Task<Dictionary<Guid, string?>> GetFallbackTrackCoversAsync(List<Guid> releaseIds)
    {
        List<AlbumCoverRow> trackCovers = await context
            .AlbumTrack.AsNoTracking()
            .Where(link => releaseIds.Contains(link.AlbumId) && link.Track.Cover != null)
            .Select(link => new AlbumCoverRow(link.AlbumId, link.Track.Cover))
            .ToListAsync();

        Dictionary<Guid, string?> covers = [];
        foreach (IGrouping<Guid, AlbumCoverRow> group in trackCovers.GroupBy(row => row.ReleaseId))
            covers[group.Key] = group.First().Cover;

        return covers;
    }

    private sealed record AlbumCoverRow(Guid ReleaseId, string? Cover);
}
