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

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Database.Models.Music;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Data.Plugins;

/// <summary>
/// Music, translated for plugins.
/// <para>
/// The same contract that keeps <see cref="PluginLibraryQuery" /> honest: every
/// method projects into a record owned by the plugin abstractions, so a
/// migration is a change here and nowhere else. Read-only, every query
/// <c>AsNoTracking</c>.
/// </para>
/// </summary>
public class PluginMusicQuery(IDbContextFactory<MediaContext> contextFactory) : IPluginMusicQuery
{
    /// <summary>
    /// The most tracks one call will return, whatever the caller asked for. A
    /// plugin naming a huge page would otherwise pull an entire library into
    /// memory in one hop.
    /// </summary>
    private const int MaxPageSize = 1000;

    public async Task<IReadOnlyList<PluginTrack>> GetTracksAsync(
        string? libraryId = null,
        int skip = 0,
        int take = 500,
        CancellationToken ct = default
    )
    {
        if (!PluginLibraryFilter.TryRead(libraryId, out Ulid? library))
            return [];

        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        IQueryable<LibraryTrack> libraryTracks = context.LibraryTrack.AsNoTracking().Playable();

        if (library is { } only)
        {
            libraryTracks = libraryTracks.Where(libraryTrack => libraryTrack.LibraryId == only);
        }

        return await ReadTracksAsync(
            context,
            libraryTracks
                // Ordered because a page without one is a page that can repeat
                // or skip rows between calls.
                .OrderBy(libraryTrack => libraryTrack.TrackId)
                .Skip(Math.Max(0, skip))
                .Take(Math.Clamp(take, 1, MaxPageSize)),
            ct
        );
    }

    public async Task<IReadOnlyList<PluginTrack>> GetTracksByIdAsync(
        IReadOnlyList<Guid> trackIds,
        CancellationToken ct = default
    )
    {
        if (trackIds.Count == 0)
        {
            return [];
        }

        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        Guid[] ids = trackIds.Distinct().Take(MaxPageSize).ToArray();

        return await ReadTracksAsync(
            context,
            context
                .LibraryTrack.AsNoTracking()
                .Playable()
                .Where(libraryTrack => ids.Contains(libraryTrack.TrackId))
                // By track as a page is, then by library, so a track that is
                // in two libraries comes back in the same order every time.
                .OrderBy(libraryTrack => libraryTrack.TrackId)
                .ThenBy(libraryTrack => libraryTrack.LibraryId),
            ct
        );
    }

    /// <summary>
    /// The one projection both track readers share, so a track read by its id
    /// is the track a page of the library shows, field for field.
    /// </summary>
    private static async Task<IReadOnlyList<PluginTrack>> ReadTracksAsync(
        MediaContext context,
        IQueryable<LibraryTrack> libraryTracks,
        CancellationToken ct
    )
    {
        List<TrackRow> rows = await libraryTracks
            .Select(libraryTrack => new TrackRow(
                libraryTrack.Track.Id,
                libraryTrack.Track.Name,
                libraryTrack
                    .Track.AlbumTrack.Select(albumTrack => albumTrack.Album.Name)
                    .FirstOrDefault(),
                libraryTrack
                    .Track.ArtistTrack.Select(artistTrack => artistTrack.Artist.Name)
                    .FirstOrDefault(),
                libraryTrack.Track.TrackNumber,
                libraryTrack.Track.DiscNumber,
                libraryTrack.Track.Duration,
                libraryTrack.LibraryId.ToString(),
                libraryTrack.Track.Date
            ))
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return [];
        }

        TrackDetails details = await ReadDetailsAsync(
            context,
            rows.Select(row => row.Id).Distinct().ToArray(),
            ct
        );

        return rows.Select(row => WithDetails(row, details)).ToList();
    }

    private static PluginTrack WithDetails(TrackRow row, TrackDetails details)
    {
        List<AlbumLink> albums = NamedFirst(details.Albums[row.Id], row.Album, album => album.Name);
        List<ArtistLink> artists = NamedFirst(
            details.Artists[row.Id],
            row.Artist,
            artist => artist.Name
        );

        return new(
            row.Id,
            row.Title,
            row.Album,
            row.Artist,
            row.TrackNumber,
            row.DiscNumber,
            ParseDurationSeconds(row.Duration),
            row.LibraryId
        )
        {
            Genres = GenresOf(row.Id, albums, details),
            Year = YearOf(row.Date, albums),
            AlbumId = albums.Count > 0 ? albums[0].AlbumId : null,
            Albums = [.. albums.Select(album => new PluginTrackAlbum(album.AlbumId, album.Name))],
            Artists =
            [
                .. artists.Select(artist => new PluginTrackArtist(artist.ArtistId, artist.Name)),
            ],
        };
    }

    /// <summary>
    /// The albums, artists and genres of a page of tracks: one flat query per
    /// link table, put together in memory. SQLite cannot nest them inside the
    /// page's own projection.
    /// </summary>
    private static async Task<TrackDetails> ReadDetailsAsync(
        MediaContext context,
        Guid[] trackIds,
        CancellationToken ct
    )
    {
        List<AlbumLink> albums = await context
            .AlbumTrack.AsNoTracking()
            .Where(link => trackIds.Contains(link.TrackId))
            .Select(link => new AlbumLink(
                link.TrackId,
                link.AlbumId,
                link.Album.Name,
                link.Album.Year
            ))
            .ToListAsync(ct);

        List<ArtistLink> artists = await context
            .ArtistTrack.AsNoTracking()
            .Where(link => trackIds.Contains(link.TrackId))
            .Select(link => new ArtistLink(link.TrackId, link.ArtistId, link.Artist.Name))
            .ToListAsync(ct);

        List<GenreLink> trackGenres = await context
            .MusicGenreTrack.AsNoTracking()
            .Where(link => trackIds.Contains(link.TrackId))
            .Select(link => new GenreLink(link.TrackId, link.Genre.Name))
            .ToListAsync(ct);

        Guid[] albumIds = albums.Select(album => album.AlbumId).Distinct().ToArray();
        List<GenreLink> albumGenres = await context
            .AlbumMusicGenre.AsNoTracking()
            .Where(link => albumIds.Contains(link.AlbumId))
            .Select(link => new GenreLink(link.AlbumId, link.MusicGenre.Name))
            .ToListAsync(ct);

        return new(
            albums.ToLookup(album => album.TrackId),
            artists.ToLookup(artist => artist.TrackId),
            trackGenres.ToLookup(genre => genre.OwnerId, genre => genre.Name),
            albumGenres.ToLookup(genre => genre.OwnerId, genre => genre.Name)
        );
    }

    /// <summary>
    /// By name, with the entry the single-name member already shows moved to
    /// the front, so the list and that member never disagree about which one
    /// comes first.
    /// </summary>
    private static List<T> NamedFirst<T>(
        IEnumerable<T> entries,
        string? shown,
        Func<T, string> name
    )
    {
        List<T> ordered = entries.OrderBy(name, StringComparer.OrdinalIgnoreCase).ToList();
        int first = shown is null
            ? -1
            : ordered.FindIndex(entry =>
                string.Equals(name(entry), shown, StringComparison.Ordinal)
            );

        if (first > 0)
        {
            T entry = ordered[first];
            ordered.RemoveAt(first);
            ordered.Insert(0, entry);
        }

        return ordered;
    }

    /// <summary>The track's own genres; where it has none, those of its albums.</summary>
    private static List<string> GenresOf(Guid trackId, List<AlbumLink> albums, TrackDetails details)
    {
        IEnumerable<string> names = details.TrackGenres[trackId];

        if (!names.Any())
        {
            names = albums.SelectMany(album => details.AlbumGenres[album.AlbumId]);
        }

        return names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The track's own release year, or else that of the album shown for it.</summary>
    private static int? YearOf(DateTime? released, List<AlbumLink> albums)
    {
        if (released is { Year: > 1 } date)
        {
            return date.Year;
        }

        return albums.Count > 0 && albums[0].Year > 0 ? albums[0].Year : null;
    }

    /// <summary>
    /// The library stores a duration as ffprobe's "hh:mm:ss" with a leading
    /// "00:" stripped, so a track under an hour reads "mm:ss". Parsed by hand:
    /// <see cref="TimeSpan.TryParse(string?, out TimeSpan)" /> takes two parts
    /// as hours and minutes and would make a 3:45 track last all afternoon.
    /// </summary>
    private static double? ParseDurationSeconds(string? duration)
    {
        if (string.IsNullOrWhiteSpace(duration))
        {
            return null;
        }

        string[] parts = duration.Split(':');

        if (parts.Length is < 2 or > 3)
        {
            return null;
        }

        double seconds = 0;
        foreach (string part in parts)
        {
            if (
                !double.TryParse(
                    part,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double value
                )
            )
            {
                return null;
            }

            seconds = seconds * 60 + value;
        }

        return seconds;
    }

    /// <summary>
    /// The shape one page comes off the database in. Duration stays a string
    /// here because SQLite cannot run the conversion; it happens in memory.
    /// </summary>
    private sealed record TrackRow(
        Guid Id,
        string Title,
        string? Album,
        string? Artist,
        int? TrackNumber,
        int? DiscNumber,
        string? Duration,
        string LibraryId,
        DateTime? Date
    );

    private sealed record AlbumLink(Guid TrackId, Guid AlbumId, string Name, int Year);

    private sealed record ArtistLink(Guid TrackId, Guid ArtistId, string Name);

    /// <param name="OwnerId">The track or the album the genre belongs to.</param>
    private sealed record GenreLink(Guid OwnerId, string Name);

    /// <summary>What a page of tracks is on, by and of, keyed by track or by album.</summary>
    private sealed record TrackDetails(
        ILookup<Guid, AlbumLink> Albums,
        ILookup<Guid, ArtistLink> Artists,
        ILookup<Guid, string> TrackGenres,
        ILookup<Guid, string> AlbumGenres
    );
}
