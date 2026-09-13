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

using FlexLabs.EntityFrameworkCore.Upsert;
using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Database.Models.Users;
using NoMercy.NmSystem.Domain;

namespace NoMercy.Data.Repositories;

public class UserDataRepository(IDbContextFactory<MediaContext> contextFactory)
    : IUserDataRepository
{
    public async Task<List<UserData>> GetUserDataAsync(
        Guid userId,
        string type,
        int? intId,
        Ulid? ulidId,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        IQueryable<UserData>? query = BuildQuery(context, userId, type, intId, ulidId);
        return query is null ? [] : await query.ToListAsync(ct);
    }

    public async Task<UserData?> GetUserDataSingleAsync(
        Guid userId,
        string type,
        int? intId,
        Ulid? ulidId,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        IQueryable<UserData>? query = BuildQuery(context, userId, type, intId, ulidId);
        return query is null ? null : await query.FirstOrDefaultAsync(ct);
    }

    public async Task<int> DeleteUserDataAsync(
        List<UserData> userData,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        context.UserData.RemoveRange(userData);
        return await context.SaveChangesAsync(ct);
    }

    public async Task<int> RemoveForItemAsync(
        Guid userId,
        string type,
        int? intId,
        Ulid? ulidId,
        CancellationToken ct = default
    )
    {
        // Guard: a null id must not fall through to `Column == null`, which would
        // match every row whose other id columns are null — a mass-delete. Require
        // the id for the requested type to be present before deleting anything.
        bool hasId = type switch
        {
            MediaTypes.MovieMediaType or MediaTypes.TvMediaType or MediaTypes.CollectionMediaType =>
                intId is not null,
            MediaTypes.SpecialMediaType => ulidId is not null,
            _ => false,
        };
        if (!hasId)
            return 0;

        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        IQueryable<UserData>? query = BuildQuery(context, userId, type, intId, ulidId);
        if (query is null)
            return 0;

        // Hidden, not deleted. "Watched" means leave the continue-watching row,
        // and this took the resume point of every episode of the show with it —
        // finishing one episode erased where the viewer was in all the others,
        // with nothing to restore from. RemovedFromContinueWatching is the flag
        // that exists for exactly this, and the list already filters on it.
        return await query.ExecuteUpdateAsync(
            setters => setters.SetProperty(data => data.RemovedFromContinueWatching, true),
            ct
        );
    }

    public async Task<int> HideFromContinueWatchingAsync(
        IEnumerable<UserData> userData,
        CancellationToken ct = default
    )
    {
        List<Ulid> ids = [.. userData.Select(data => data.Id)];
        if (ids.Count == 0)
            return 0;

        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        return await context
            .UserData.Where(data => ids.Contains(data.Id))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(data => data.RemovedFromContinueWatching, true),
                ct
            );
    }

    private IQueryable<UserData>? BuildQuery(
        MediaContext context,
        Guid userId,
        string type,
        int? intId,
        Ulid? ulidId
    )
    {
        IQueryable<UserData> query = context
            .UserData.AsNoTracking()
            .Where(data => data.UserId.Equals(userId));

        return type switch
        {
            MediaTypes.MovieMediaType => query.Where(data => data.MovieId == intId),
            MediaTypes.TvMediaType => query.Where(data => data.TvId == intId),
            MediaTypes.SpecialMediaType => query.Where(data => data.SpecialId == ulidId),
            MediaTypes.CollectionMediaType => query.Where(data => data.CollectionId == intId),
            _ => null,
        };
    }

    public async Task<bool> UpsertWatchProgressAsync(
        WatchProgress progress,
        CancellationToken ct = default
    )
    {
        int? movieId = progress.PlaylistType == MediaTypes.MovieMediaType ? progress.TmdbId : null;
        int? tvId = progress.PlaylistType is MediaTypes.TvMediaType or MediaTypes.AnimeMediaType
            ? progress.TmdbId
            : null;
        int? collectionId = null;
        Ulid? specialId = null;

        switch (progress.PlaylistType)
        {
            case MediaTypes.MovieMediaType:
            case MediaTypes.TvMediaType:
            case MediaTypes.AnimeMediaType:
                break;
            case MediaTypes.CollectionMediaType
                when int.TryParse(progress.PlaylistId, out int parsedCollection):
                collectionId = parsedCollection;
                break;
            case MediaTypes.SpecialMediaType
                when Ulid.TryParse(progress.PlaylistId, out Ulid parsedSpecial):
                specialId = parsedSpecial;
                break;
            default:
                return false;
        }

        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        if (!await context.VideoFiles.AnyAsync(file => file.Id == progress.VideoFileId, ct))
            return false;
        if (movieId is not null && !await context.Movies.AnyAsync(m => m.Id == movieId, ct))
            return false;
        if (tvId is not null && !await context.Tvs.AnyAsync(t => t.Id == tvId, ct))
            return false;
        if (
            collectionId is not null
            && !await context.Collections.AnyAsync(c => c.Id == collectionId, ct)
        )
            return false;
        if (specialId is not null && !await context.Specials.AnyAsync(s => s.Id == specialId, ct))
            return false;

        UserData row = new()
        {
            UserId = progress.UserId,
            Type = progress.PlaylistType,
            Time = progress.Time,
            VideoFileId = progress.VideoFileId,
            Audio = progress.Audio,
            Subtitle = progress.Subtitle,
            SubtitleType = progress.SubtitleType,
            MovieId = movieId,
            TvId = tvId,
            CollectionId = collectionId,
            SpecialId = specialId,
        };

        UpsertCommandBuilder<UserData> query = context.UserData.Upsert(row);
        query = progress.PlaylistType switch
        {
            MediaTypes.MovieMediaType => query.On(x => new
            {
                x.VideoFileId,
                x.UserId,
                x.MovieId,
            }),
            MediaTypes.CollectionMediaType => query.On(x => new
            {
                x.VideoFileId,
                x.UserId,
                x.CollectionId,
            }),
            MediaTypes.SpecialMediaType => query.On(x => new
            {
                x.VideoFileId,
                x.UserId,
                x.SpecialId,
            }),
            _ => query.On(x => new
            {
                x.VideoFileId,
                x.UserId,
                x.TvId,
            }),
        };

        await query
            .WhenMatched(
                (stored, incoming) =>
                    new()
                    {
                        Id = stored.Id,
                        Type = incoming.Type,
                        MovieId = incoming.MovieId,
                        TvId = incoming.TvId,
                        CollectionId = incoming.CollectionId,
                        SpecialId = incoming.SpecialId,
                        Time = incoming.Time,
                        Audio = incoming.Audio,
                        Subtitle = incoming.Subtitle,
                        SubtitleType = incoming.SubtitleType,
                        LastPlayedDate = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                        RemovedFromContinueWatching = false,
                    }
            )
            .RunAsync(ct);

        return true;
    }
}
