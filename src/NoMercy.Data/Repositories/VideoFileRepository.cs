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
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.TvShows;

namespace NoMercy.Data.Repositories;

public class VideoFileRepository(IDbContextFactory<MediaContext> contextFactory)
    : IVideoFileRepository
{
    public async Task<VideoFile?> GetByIdAsync(Ulid id, CancellationToken ct = default)
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        return await context
            .VideoFiles.AsNoTracking()
            .FirstOrDefaultAsync(file => file.Id == id, ct);
    }

    public async Task<VideoFile?> GetForUserWithMetadataAsync(
        Ulid id,
        Guid userId,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        VideoFile? file = await context
            .VideoFiles.AsNoTracking()
            .Include(vf => vf.Metadata)
            .FirstOrDefaultAsync(vf => vf.Id == id, ct);
        if (file is null)
            return null;

        if (
            file.MovieId is int movieId
            && await context.Movies.AnyAsync(
                m => m.Id == movieId && m.Library.LibraryUsers.Any(u => u.UserId == userId),
                ct
            )
        )
            return file;

        if (
            file.EpisodeId is int episodeId
            && await context.Episodes.AnyAsync(
                e => e.Id == episodeId && e.Tv.Library.LibraryUsers.Any(u => u.UserId == userId),
                ct
            )
        )
            return file;

        return null;
    }

    public async Task<bool> IsLibraryFolderAsync(Ulid folderId, CancellationToken ct = default)
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        return await context.Folders.AsNoTracking().AnyAsync(f => f.Id == folderId, ct);
    }

    public async Task<Metadata?> GetMetadataAsync(Ulid id, CancellationToken ct = default)
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        VideoFile? file = await context
            .VideoFiles.AsNoTracking()
            .Include(videoFile => videoFile.Metadata)
            .FirstOrDefaultAsync(videoFile => videoFile.Id == id, ct);
        return file?.Metadata;
    }

    public async Task<bool> ExistsAsync(Ulid id, CancellationToken ct = default)
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        return await context.VideoFiles.AsNoTracking().AnyAsync(file => file.Id == id, ct);
    }

    public async Task<bool> ExistsAtHostPathAsync(string hostPath, CancellationToken ct = default)
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        return await context
            .VideoFiles.AsNoTracking()
            .AnyAsync(file => file.HostFolder + "/" + file.Filename == hostPath, ct);
    }

    public async Task<List<Episode>> GetEncodedEpisodesForSeasonAsync(
        int seasonId,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        return await context
            .Episodes.AsNoTracking()
            .Include(episode => episode.VideoFiles)
            .Where(episode => episode.SeasonId == seasonId && episode.VideoFiles.Count > 0)
            .OrderBy(episode => episode.EpisodeNumber)
            .ThenBy(episode => episode.Id)
            .ToListAsync(ct);
    }

    public async Task<int> GetShowIdForSeasonAsync(int seasonId, CancellationToken ct = default)
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        return await context
            .Seasons.AsNoTracking()
            .Where(season => season.Id == seasonId)
            .Select(season => season.TvId)
            .FirstOrDefaultAsync(ct);
    }
}
