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
using NoMercy.Providers.FanArt.Client;

namespace NoMercy.Data.Repositories;

/// <summary>
/// Picks the album covers a music group card shows for a group with no image
/// of its own (a genre, a playlist without a cover), by the same rules as
/// <see cref="GroupItemPosters"/>. Each poster's <see cref="GroupPoster.Src"/>
/// is the stored cover path; the card turns it into the served URL.
/// </summary>
public static class MusicGroupCovers
{
    internal record CoverRow(
        Guid GroupId,
        Guid AlbumId,
        DateTime AddedAt,
        string Name,
        string Cover,
        string? Palette
    );

    public static async Task<List<MusicGenreCardDto>> FillGenresAsync(
        MediaContext context,
        Guid userId,
        List<MusicGenreCardDto> genres,
        CancellationToken ct
    )
    {
        Dictionary<Guid, GroupPoster[]> covers = await ForGenresAsync(
            context,
            userId,
            [.. genres.Select(genre => genre.Id)],
            ct
        );
        foreach (MusicGenreCardDto genre in genres)
            genre.ItemPosters = covers.GetValueOrDefault(genre.Id) ?? [];
        return genres;
    }

    /// <summary>Only playlists without a cover of their own get covers: the others keep theirs.</summary>
    public static async Task<List<PlaylistCardDto>> FillPlaylistsAsync(
        MediaContext context,
        List<PlaylistCardDto> playlists,
        CancellationToken ct
    )
    {
        List<Guid> coverless =
        [
            .. playlists.Where(playlist => !playlist.HasOwnCover).Select(playlist => playlist.Id),
        ];
        Dictionary<Guid, GroupPoster[]> covers = await ForPlaylistsAsync(context, coverless, ct);
        foreach (PlaylistCardDto playlist in playlists)
            playlist.ItemPosters = covers.GetValueOrDefault(playlist.Id) ?? [];
        return playlists;
    }

    public static async Task<Dictionary<Guid, GroupPoster[]>> ForGenresAsync(
        MediaContext context,
        Guid userId,
        List<Guid> genreIds,
        CancellationToken ct
    )
    {
        if (genreIds.Count == 0)
            return [];

        // A genre spans libraries; only covers from the user's own libraries show.
        List<CoverRow> rows = await context
            .AlbumMusicGenre.AsNoTracking()
            .Where(link => genreIds.Contains(link.MusicGenreId))
            .Where(link => link.Album.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Where(link => link.Album.Cover != null && link.Album.Cover != "")
            .Where(link => link.Album._colorPalette != null && link.Album._colorPalette != "")
            .Select(link => new CoverRow(
                link.MusicGenreId,
                link.AlbumId,
                link.Album.CreatedAt,
                link.Album.Name,
                link.Album.Cover!,
                link.Album._colorPalette
            ))
            .ToListAsync(ct);

        return await Pick(rows);
    }

    public static async Task<Dictionary<Guid, GroupPoster[]>> ForPlaylistsAsync(
        MediaContext context,
        List<Guid> playlistIds,
        CancellationToken ct
    )
    {
        if (playlistIds.Count == 0)
            return [];

        // A plain join: SQLite cannot run the APPLY a SelectMany over Track.AlbumTrack needs.
        List<CoverRow> rows = await context
            .PlaylistTrack.AsNoTracking()
            .Where(entry => playlistIds.Contains(entry.PlaylistId))
            .Join(
                context.AlbumTrack,
                entry => entry.TrackId,
                albumTrack => albumTrack.TrackId,
                (entry, albumTrack) => new { entry.PlaylistId, albumTrack.Album }
            )
            .Where(row => row.Album.Cover != null && row.Album.Cover != "")
            .Where(row => row.Album._colorPalette != null && row.Album._colorPalette != "")
            .Select(row => new CoverRow(
                row.PlaylistId,
                row.Album.Id,
                row.Album.CreatedAt,
                row.Album.Name,
                row.Album.Cover!,
                row.Album._colorPalette
            ))
            .ToListAsync(ct);

        return await Pick(rows);
    }

    private static Task<Dictionary<Guid, GroupPoster[]>> Pick(List<CoverRow> rows) =>
        PickAsync(rows, cover => FanArtImageClient.IsStored(Path.GetFileName(cover)));

    // A cover counts only with its palette and its file on the server: older
    // importers wrote a cover name with a palette but never stored the file.
    internal static async Task<Dictionary<Guid, GroupPoster[]>> PickAsync(
        List<CoverRow> rows,
        Func<string, Task<bool>> isStored
    )
    {
        Dictionary<Guid, GroupPoster[]> picked = [];
        IEnumerable<IGrouping<Guid, (CoverRow Row, PaletteColors? Palette)>> groups = rows.Select(
                row => (Row: row, Palette: ColorPalette.FromJsonOrNull(row.Palette)?.Cover)
            )
            .Where(cover => cover.Palette is not null)
            .GroupBy(cover => cover.Row.GroupId);

        foreach (IGrouping<Guid, (CoverRow Row, PaletteColors? Palette)> group in groups)
        {
            List<(CoverRow Row, PaletteColors? Palette)> stored = [];
            foreach (
                (CoverRow Row, PaletteColors? Palette) cover in group
                    .OrderBy(cover => cover.Row.AddedAt)
                    .ThenBy(cover => cover.Row.Name, StringComparer.Ordinal)
                    .ThenBy(cover => cover.Row.AlbumId)
                    .DistinctBy(cover => cover.Row.Cover)
            )
            {
                if (stored.Count == GroupItemPosters.Max)
                    break;
                if (await isStored(cover.Row.Cover))
                    stored.Add(cover);
            }

            picked[group.Key] =
            [
                .. stored.Select(
                    (cover, index) =>
                        new GroupPoster(
                            cover.Row.Cover,
                            index < GroupItemPosters.FrontPosters ? cover.Palette : null,
                            CardColor.Pick(cover.Palette)
                        )
                ),
            ];
        }

        return picked;
    }
}
