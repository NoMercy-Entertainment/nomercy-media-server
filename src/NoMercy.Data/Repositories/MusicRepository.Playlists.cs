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
using NoMercy.Data.DTOs;
using NoMercy.Database;
using NoMercy.Database.Models.Music;

namespace NoMercy.Data.Repositories;

public partial class MusicRepository
{
    #region Playlist Queries

    public async Task<List<CarouselResponseItemDto>> GetCarouselPlaylistsAsync(
        Guid userId,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .Playlists.AsNoTracking()
            .Where(playlist => playlist.UserId == userId)
            .Include(playlist => playlist.Tracks)
                .ThenInclude(trackUser => trackUser.Track)
            .OrderBy(playlist => playlist.Name)
            .ThenBy(playlist => playlist.Id)
            .Select(playlist => new CarouselResponseItemDto(playlist))
            .Take(36)
            .ToListAsync(ct);
    }

    public async Task<Playlist?> GetPlaylistAsync(
        Guid userId,
        Guid id,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .Playlists.AsNoTracking()
            .Where(playlist => playlist.Id == id)
            .Where(playlist => playlist.UserId == userId)
            .Include(playlist => playlist.Tracks)
                .ThenInclude(trackUser => trackUser.Track)
                    .ThenInclude(track => track.AlbumTrack)
                        .ThenInclude(albumTrack => albumTrack.Album)
                            .ThenInclude(album => album.AlbumArtist)
                                .ThenInclude(albumArtist => albumArtist.Artist)
                                    .ThenInclude(artist => artist.Images)
            .Include(playlist => playlist.Tracks)
                .ThenInclude(trackUser => trackUser.Track)
                    .ThenInclude(track => track.ArtistTrack)
                        .ThenInclude(artistTrack => artistTrack.Artist)
                            .ThenInclude(artist => artist.Images)
            .FirstOrDefaultAsync(ct);
    }

    #endregion

    #region Playlist Management

    // Rooted directly at PlaylistTrack (never through Playlist.Tracks) — an
    // AsNoTracking() Include that leaves this root type and comes back to it via a
    // collection navigation (e.g. Playlist.Tracks, the inverse of this same FK) is
    // a genuine cycle to EF Core's no-tracking validator and throws unconditionally:
    // "The Include path 'Playlist->Tracks' results in a cycle." This shape never
    // revisits PlaylistTrack, so it can't recreate that cycle.
    public async Task<List<PlaylistTrack>> GetPlaylistTracksAsync(
        Guid userId,
        Guid playlistId,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        return await mediaContext
            .PlaylistTrack.AsNoTracking()
            .Where(pt => pt.PlaylistId == playlistId && pt.Playlist.UserId == userId)
            .Include(pt => pt.Track)
                .ThenInclude(track => track.AlbumTrack)
                    .ThenInclude(albumTrack => albumTrack.Album)
                        .ThenInclude(album => album.AlbumArtist)
                            .ThenInclude(albumArtist => albumArtist.Artist)
                                .ThenInclude(artist => artist.Images)
            .Include(pt => pt.Track)
                .ThenInclude(track => track.ArtistTrack)
                    .ThenInclude(artistTrack => artistTrack.Artist)
                        .ThenInclude(artist => artist.Images)
            .Include(pt => pt.Track)
                .ThenInclude(track => track.TrackUser)
            .ToListAsync(ct);
    }

    // Three flat queries, never one rooted through Album.AlbumTrack (that revisits this same
    // entity type — the identical no-tracking cycle shape as the playlist/artist bugs above;
    // this call site ran tracked, dodging the crash, but still forced the same split-query
    // correlation to re-derive the join per branch under SQLite).
    //
    // Step 1 roots at AlbumTrack directly (WHERE AlbumId = @albumId, indexed) — that alone
    // already yields every track on the album, so there is no need to detour through
    // Album.AlbumTrack at all. Track.ArtistTrack is a different entity type than the root
    // and can be included directly without recreating the cycle.
    //
    // Step 2 roots at Track (not AlbumTrack) to fetch which OTHER albums each track also
    // belongs to: Track.AlbumTrack is the same entity type as step 1's root, and chaining it
    // from an AlbumTrack root would revisit that type — the identical no-tracking cycle
    // shape as the playlist bug, just one hop deeper. Rooting at Track instead avoids it.
    // Deliberately stops at the join row — it does NOT chase ThenInclude(Album).Images/
    // Translations here, because every track on THIS album shares the same handful of
    // albums; doing so joined Images/Translations once PER TRACK ROW instead of once per
    // distinct album, which measured 15-19 real seconds (nearly all of it kernel/IO time,
    // not CPU — confirmed via a raw sqlite3 execution of the exact generated SQL, reproduced
    // consistently on a second run, ruling out a cold-cache/cold-JIT explanation) against
    // the real dev DB for a 38-track album with a shared Translations table carrying
    // 500k+ rows across every entity type.
    //
    // Step 3 roots at Album directly (WHERE Id IN distinctAlbumIds — typically just one row)
    // to fetch Images/Translations exactly once per distinct album, however many tracks
    // reference it, then attaches the result onto each AlbumTrack.Album in memory.
    public async Task<List<AlbumTrack>> GetAlbumTracksAsync(
        Guid userId,
        Guid albumId,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);

        List<AlbumTrack> albumTracks = await mediaContext
            .AlbumTrack.AsNoTracking()
            .Where(at => at.AlbumId == albumId)
            .Include(at => at.Track)
                .ThenInclude(track => track.ArtistTrack)
                    .ThenInclude(artistTrack => artistTrack.Artist)
                        .ThenInclude(artist => artist.Images)
            .Include(at => at.Track)
                .ThenInclude(track => track.TrackUser)
            .ToListAsync(ct);

        if (albumTracks.Count == 0)
            return albumTracks;

        List<Guid> trackIds = [.. albumTracks.Select(at => at.TrackId).Distinct()];
        List<Track> tracksWithAlbumLinks = await mediaContext
            .Tracks.AsNoTracking()
            .Where(track => trackIds.Contains(track.Id))
            .Include(track => track.AlbumTrack)
            .ToListAsync(ct);

        List<Guid> distinctAlbumIds =
        [
            .. tracksWithAlbumLinks
                .SelectMany(track => track.AlbumTrack)
                .Select(at => at.AlbumId)
                .Distinct(),
        ];

        List<Album> albumsWithDetails = await mediaContext
            .Albums.AsNoTracking()
            .Where(album => distinctAlbumIds.Contains(album.Id))
            .Include(album => album.Images)
            .Include(album => album.Translations)
            .Include(album => album.AlbumArtist)
                .ThenInclude(albumArtist => albumArtist.Artist)
                    .ThenInclude(artist => artist.Images)
            .ToListAsync(ct);

        Dictionary<Guid, Album> albumById = albumsWithDetails.ToDictionary(album => album.Id);

        foreach (Track track in tracksWithAlbumLinks)
        foreach (AlbumTrack albumTrack in track.AlbumTrack)
            if (albumById.TryGetValue(albumTrack.AlbumId, out Album? album))
                albumTrack.Album = album;

        Dictionary<Guid, ICollection<AlbumTrack>> albumsByTrackId =
            tracksWithAlbumLinks.ToDictionary(track => track.Id, track => track.AlbumTrack);

        foreach (AlbumTrack albumTrack in albumTracks)
            if (
                albumsByTrackId.TryGetValue(albumTrack.TrackId, out ICollection<AlbumTrack>? albums)
            )
                albumTrack.Track.AlbumTrack = albums;

        return albumTracks;
    }

    // Three flat queries, never one rooted through Artist.ArtistTrack (that combines three
    // chained one-to-many collections — ArtistTrack, AlbumTrack, Translations — behind a
    // single-row filter, which forces SQLite's split-query correlation to re-derive the
    // same 5-table join per branch; that shape measured 80-100s for a 153-track artist).
    //
    // Step 1 roots at ArtistTrack directly (WHERE ArtistId = @artistId, indexed), so every
    // branch off it is one collection hop from an already-flat, already-filtered result.
    // Deliberately stops at the AlbumTrack join row — it does NOT chase
    // ThenInclude(Album).Translations here, for the identical reason as the album fix:
    // tracks on one artist very often share the same handful of albums, and chasing
    // Album.Translations per track re-derives it once per track instead of once per
    // distinct album. This redundancy is what actually measured 80-100s live for a
    // 153-track artist — worse than the album case because an artist's tracks span many
    // albums, so this join re-derives EVERY one of them repeatedly, not just one.
    //
    // Step 2 roots at Track (not ArtistTrack) to fetch the full artist-credit list per
    // track: Track.ArtistTrack is the same entity type as step 1's root, and chaining it
    // from an ArtistTrack root would revisit that type — the identical no-tracking cycle
    // shape as the playlist bug above, just one hop deeper. Rooting at Track instead avoids
    // it, and the two result sets are attached in memory.
    //
    // Step 3 roots at Album directly (WHERE Id IN distinctAlbumIds) to fetch Translations
    // exactly once per distinct album, however many of this artist's tracks reference it,
    // then attaches the result onto each AlbumTrack.Album in memory — mirrors
    // GetAlbumTracksAsync's step 3 exactly.
    public async Task<List<ArtistTrack>> GetArtistTracksAsync(
        Guid userId,
        Guid artistId,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);

        List<ArtistTrack> artistTracks = await mediaContext
            .ArtistTrack.AsNoTracking()
            .Where(at => at.ArtistId == artistId)
            .Include(at => at.Artist)
                .ThenInclude(artist => artist.Images)
            .Include(at => at.Track)
                .ThenInclude(track => track.AlbumTrack)
            .Include(at => at.Track)
                .ThenInclude(track => track.TrackUser)
            .ToListAsync(ct);

        if (artistTracks.Count == 0)
            return artistTracks;

        List<Guid> trackIds = [.. artistTracks.Select(at => at.TrackId).Distinct()];
        List<Track> tracksWithCredits = await mediaContext
            .Tracks.AsNoTracking()
            .Where(track => trackIds.Contains(track.Id))
            .Include(track => track.ArtistTrack)
                .ThenInclude(artistTrack => artistTrack.Artist)
                    .ThenInclude(artist => artist.Images)
            .ToListAsync(ct);

        Dictionary<Guid, ICollection<ArtistTrack>> creditsByTrackId =
            tracksWithCredits.ToDictionary(track => track.Id, track => track.ArtistTrack);

        foreach (ArtistTrack artistTrack in artistTracks)
            if (
                creditsByTrackId.TryGetValue(
                    artistTrack.TrackId,
                    out ICollection<ArtistTrack>? credits
                )
            )
                artistTrack.Track.ArtistTrack = credits;

        List<Guid> distinctAlbumIds =
        [
            .. artistTracks
                .SelectMany(at => at.Track.AlbumTrack)
                .Select(albumTrack => albumTrack.AlbumId)
                .Distinct(),
        ];

        List<Album> albumsWithTranslations = await mediaContext
            .Albums.AsNoTracking()
            .Where(album => distinctAlbumIds.Contains(album.Id))
            .Include(album => album.Translations)
            .Include(album => album.AlbumArtist)
                .ThenInclude(albumArtist => albumArtist.Artist)
                    .ThenInclude(artist => artist.Images)
            .ToListAsync(ct);

        Dictionary<Guid, Album> albumById = albumsWithTranslations.ToDictionary(album => album.Id);

        foreach (ArtistTrack artistTrack in artistTracks)
        foreach (AlbumTrack albumTrack in artistTrack.Track.AlbumTrack)
            if (albumById.TryGetValue(albumTrack.AlbumId, out Album? album))
                albumTrack.Album = album;

        return artistTracks;
    }

    // Two flat queries, never one rooted through Genre.MusicGenreTracks (that revisits this
    // same entity type — the identical no-tracking cycle shape as the playlist/artist/album
    // bugs above; this call site ran tracked, dodging the crash, but still forced the same
    // split-query correlation to re-derive the join three times over, once per branch).
    //
    // Step 1 roots directly at MusicGenreTrack (WHERE GenreId = @genreId, indexed) — already
    // yields every track in the genre, so there is no need to detour through
    // Genre.MusicGenreTracks at all. ArtistTrack.Artist and TrackUser hang off Track (a
    // different entity type than the root) and can be included directly without recreating
    // the cycle — unlike the playlist/artist/album fixes, revisiting MusicGenreTrack was
    // never the issue here, so no second query is needed just to dodge that. Deliberately
    // stops at the AlbumTrack join row for the SAME reason as the album/artist fixes: many
    // tracks in a genre share the same handful of albums, and chasing
    // Album.AlbumArtist.Artist.Images / Album.Translations per track re-derives them once
    // per track instead of once per distinct album.
    //
    // Step 2 roots at Album directly (WHERE Id IN distinctAlbumIds) to fetch
    // AlbumArtist.Artist.Images and Translations exactly once per distinct album, however
    // many tracks in the genre reference it, then attaches the result onto each
    // AlbumTrack.Album in memory — mirrors GetAlbumTracksAsync's step 3 exactly.
    public async Task<List<MusicGenreTrack>> GetGenreTracksAsync(
        Guid userId,
        Guid genreId,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);

        List<MusicGenreTrack> genreTracks = await mediaContext
            .MusicGenreTrack.AsNoTracking()
            .Where(mgt =>
                mgt.Genre.AlbumMusicGenres.Any(g =>
                    g.Album.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
                || mgt.Genre.ArtistMusicGenres.Any(g =>
                    g.Artist.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
            )
            .Where(mgt => mgt.GenreId == genreId)
            .Include(mgt => mgt.Track)
                .ThenInclude(track => track.AlbumTrack)
            .Include(mgt => mgt.Track)
                .ThenInclude(track => track.ArtistTrack)
                    .ThenInclude(artistTrack => artistTrack.Artist)
                        .ThenInclude(artist => artist.Images)
            .Include(mgt => mgt.Track)
                .ThenInclude(track => track.TrackUser)
            .ToListAsync(ct);

        if (genreTracks.Count == 0)
            return genreTracks;

        List<Guid> distinctAlbumIds =
        [
            .. genreTracks
                .SelectMany(mgt => mgt.Track.AlbumTrack)
                .Select(albumTrack => albumTrack.AlbumId)
                .Distinct(),
        ];

        List<Album> albumsWithDetails = await mediaContext
            .Albums.AsNoTracking()
            .Where(album => distinctAlbumIds.Contains(album.Id))
            .Include(album => album.AlbumArtist)
                .ThenInclude(albumArtist => albumArtist.Artist)
                    .ThenInclude(artist => artist.Images)
            .Include(album => album.Translations)
            .ToListAsync(ct);

        Dictionary<Guid, Album> albumById = albumsWithDetails.ToDictionary(album => album.Id);

        foreach (MusicGenreTrack genreTrack in genreTracks)
        foreach (AlbumTrack albumTrack in genreTrack.Track.AlbumTrack)
            if (albumById.TryGetValue(albumTrack.AlbumId, out Album? album))
                albumTrack.Album = album;

        return genreTracks;
    }

    #endregion

    #region Projection Methods — Playlist Cards

    public async Task<List<PlaylistCardDto>> GetPlaylistCardsAsync(
        Guid userId,
        int take = 36,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        List<PlaylistCardDto> playlists = await mediaContext
            .Playlists.AsNoTracking()
            .Where(playlist => playlist.UserId == userId)
            .OrderBy(playlist => playlist.Name)
            .ThenBy(playlist => playlist.Id)
            .Select(playlist => new PlaylistCardDto
            {
                Id = playlist.Id,
                Name = playlist.Name,
                Cover = playlist.Cover,
                Description = playlist.Description,
                ColorPalette = playlist._colorPalette ?? string.Empty,
                TrackCount = playlist.Tracks.Count(),
            })
            .Take(take)
            .ToListAsync(ct);
        return await MusicGroupCovers.FillPlaylistsAsync(mediaContext, playlists, ct);
    }

    public async Task<List<PlaylistCardDto>> GetPlaylistCardsByIdsAsync(
        List<Guid> playlistIds,
        CancellationToken ct = default
    )
    {
        await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(ct);
        List<PlaylistCardDto> playlists = await mediaContext
            .Playlists.AsNoTracking()
            .Where(playlist => playlistIds.Contains(playlist.Id))
            .Select(playlist => new PlaylistCardDto
            {
                Id = playlist.Id,
                Name = playlist.Name,
                Cover = playlist.Cover,
                Description = playlist.Description,
                ColorPalette = playlist._colorPalette ?? string.Empty,
                TrackCount = playlist.Tracks.Count(),
            })
            .ToListAsync(ct);
        return await MusicGroupCovers.FillPlaylistsAsync(mediaContext, playlists, ct);
    }

    #endregion
}
