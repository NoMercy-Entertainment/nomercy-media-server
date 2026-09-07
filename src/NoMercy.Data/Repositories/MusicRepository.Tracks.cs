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
using NoMercy.Database.Models.Music;
using NoMercy.NmSystem.NewtonSoftConverters;

namespace NoMercy.Data.Repositories;

public partial class MusicRepository
{
    #region Track Queries

    public async Task<Track?> GetTrackAsync(Guid id, CancellationToken ct = default)
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .Tracks.AsNoTracking()
            .Where(track => track.Id == id)
            .Include(track => track.AlbumTrack)
                .ThenInclude(albumTrack => albumTrack.Album)
                    .ThenInclude(album => album.AlbumArtist)
                        .ThenInclude(albumArtist => albumArtist.Artist)
                            .ThenInclude(artist => artist.Images)
            .Include(track => track.ArtistTrack)
                .ThenInclude(artistTrack => artistTrack.Artist)
                    .ThenInclude(artist => artist.Images)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<List<TrackUser>> GetTracks(Guid userId, CancellationToken ct = default)
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .TrackUser.AsNoTracking()
            .Where(tu => tu.UserId == userId)
            .Include(trackUser => trackUser.Track)
                .ThenInclude(track => track.AlbumTrack)
                    .ThenInclude(albumTrack => albumTrack.Album)
            .Include(trackUser => trackUser.Track)
                .ThenInclude(track => track.ArtistTrack)
                    .ThenInclude(artistTrack => artistTrack.Artist)
            .ToListAsync(ct);
    }

    public async Task LikeTrackAsync(
        Guid userId,
        Track track,
        bool liked,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        if (liked)
        {
            await mediaContext
                .TrackUser.Upsert(new(track.Id, userId))
                .On(m => new { m.TrackId, m.UserId })
                .WhenMatched(m => new() { TrackId = m.TrackId, UserId = m.UserId })
                .RunAsync();
        }
        else
        {
            TrackUser? trackUser = await mediaContext.TrackUser.FirstOrDefaultAsync(
                tu => tu.TrackId == track.Id && tu.UserId == userId,
                ct
            );

            if (trackUser is not null)
            {
                mediaContext.TrackUser.Remove(trackUser);
                await mediaContext.SaveChangesAsync(ct);
            }
        }
    }

    public async Task RecordPlaybackAsync(Guid trackId, Guid userId, CancellationToken ct = default)
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        await mediaContext.MusicPlays.AddAsync(new(userId, trackId), ct);
        await mediaContext.SaveChangesAsync(ct);
    }

    public async Task<Track?> GetTrackWithIncludesAsync(Guid id, CancellationToken ct = default)
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .Tracks.AsNoTracking()
            .Where(track => track.Id == id)
            .Include(track => track.ArtistTrack)
                .ThenInclude(artistTrack => artistTrack.Artist)
            .Include(track => track.AlbumTrack)
                .ThenInclude(albumTrack => albumTrack.Album)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<List<TrackAudioAnalysis>> GetTrackAudioAnalysisAsync(
        IReadOnlyCollection<Guid> trackIds,
        CancellationToken ct = default
    )
    {
        if (trackIds.Count == 0)
            return [];

        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .TrackAudioAnalysis.AsNoTracking()
            .Where(analysis =>
                trackIds.Contains(analysis.TrackId) && analysis.State == AudioAnalysisState.Ok
            )
            .ToListAsync(ct);
    }

    public async Task<Lyric[]?> UpdateTrackLyricsAsync(
        Track track,
        string lyricsJson,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        await mediaContext
            .Upsert(track)
            .On(v => new { v.Id })
            .WhenMatched(v => new() { _lyrics = lyricsJson })
            .RunAsync();

        return lyricsJson.FromJson<Lyric[]>();
    }

    public async Task UpdateTrackLyricsOffsetAsync(
        Track track,
        int? offsetMs,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        await mediaContext
            .Upsert(track)
            .On(v => new { v.Id })
            .WhenMatched(v => new() { LyricsOffset = offsetMs })
            .RunAsync();
    }

    #endregion
}
