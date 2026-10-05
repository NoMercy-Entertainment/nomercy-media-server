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
using Newtonsoft.Json;
using NoMercy.Database;

namespace NoMercy.Data.Repositories;

/// <summary>
/// One title inside a group (an anime theme, demographic or season), as the
/// group card's poster mosaic needs it.
/// </summary>
/// <summary>A group-to-show link, as the link table stores it.</summary>
public record GroupLink(int GroupId, int TvId);

public record GroupPosterRow(
    int GroupId,
    int ItemId,
    DateTime AddedAt,
    string TitleSort,
    string? TextlessPoster,
    string? Poster,
    int? TextlessImageId = null,
    bool IsMovie = false
)
{
    /// <summary>The poster the card shows for this title.</summary>
    public string? Path => TextlessPoster ?? Poster;
}

/// <summary>
/// A poster on a group card: its path, the one color the card draws with
/// (<see cref="CardColor.Pick"/>), and, for a poster that can be in front,
/// the palette its loading gradient needs.
/// </summary>
public record GroupPoster(
    [property: JsonProperty("src")] string Src,
    [property: JsonProperty("color_palette", NullValueHandling = NullValueHandling.Ignore)]
        PaletteColors? ColorPalette,
    [property: JsonProperty("color")] string? Color = null
);

/// <summary>
/// Picks the posters a group card shows for a group that has no image of its
/// own: the posters of the titles inside it.
/// </summary>
public static class GroupItemPosters
{
    public const int Max = 9;

    /// <summary>
    /// How many posters keep their palette: the card puts the first poster
    /// in front, or the second one in the fan look, and only the front one
    /// draws a gradient while it loads.
    /// </summary>
    public const int FrontPosters = 2;

    public const string PosterType = "poster";

    /// <summary>
    /// The poster rows of the shows in <paramref name="links"/> that have a
    /// playable episode: one query over the distinct shows, joined to the
    /// links in memory. Testing playability inside the link query runs it
    /// once per link row (4,223 rows for 409 shows on the dev library, 66 ms),
    /// and a link query filtered on both group ids and show ids makes SQLite
    /// probe the (group, show) index once per pair (115k probes, 30 ms).
    /// </summary>
    public static async Task<List<GroupPosterRow>> PlayableTvRowsAsync(
        MediaContext context,
        List<GroupLink> links,
        CancellationToken ct
    )
    {
        List<int> tvIds = [.. links.Select(link => link.TvId).Distinct()];
        if (tvIds.Count == 0)
            return [];

        Dictionary<int, GroupPosterRow> playable = await context
            .Tvs.AsNoTracking()
            .Where(tv =>
                tvIds.Contains(tv.Id)
                && tv.Episodes.Any(e => e.VideoFiles.Any(v => v.Folder != null))
            )
            .Select(tv => new GroupPosterRow(0, tv.Id, tv.CreatedAt, tv.TitleSort, null, tv.Poster))
            .ToDictionaryAsync(row => row.ItemId, ct);

        return
        [
            .. links
                .Where(link => playable.ContainsKey(link.TvId))
                .Select(link => playable[link.TvId] with { GroupId = link.GroupId }),
        ];
    }

    /// <summary>
    /// Fills <see cref="GroupPosterRow.TextlessPoster"/> for link rows fetched
    /// without it: one image query per media kind over the distinct titles,
    /// instead of a correlated subquery per link row (a title sits in many
    /// groups, so there are far more link rows than titles).
    /// </summary>
    public static async Task<List<GroupPosterRow>> WithTextlessPostersAsync(
        MediaContext context,
        List<GroupPosterRow> tvRows,
        List<GroupPosterRow> movieRows,
        CancellationToken ct
    )
    {
        List<int> tvIds = [.. tvRows.Select(row => row.ItemId).Distinct()];
        List<int> movieIds = [.. movieRows.Select(row => row.ItemId).Distinct()];

        // Queried from the title side on purpose: filtering Images by type and
        // language first makes SQLite scan every textless poster in the library
        // (89 ms on 460k images); per title it walks the (TvId, Type) index.
        // Path and id are two scalar subqueries: one subquery returning both
        // becomes a window-function join that SQLite cannot run on the index.
        Dictionary<int, (string? Path, int? Id)> tvPosters = (
            await context
                .Tvs.AsNoTracking()
                .Where(tv => tvIds.Contains(tv.Id))
                .Select(tv => new
                {
                    tv.Id,
                    Poster = tv
                        .Images.Where(image => image.Type == PosterType && image.Iso6391 == null)
                        .OrderByDescending(image => image.VoteAverage)
                        .ThenBy(image => image.Id)
                        .Select(image => image.FilePath)
                        .FirstOrDefault(),
                    ImageId = tv
                        .Images.Where(image => image.Type == PosterType && image.Iso6391 == null)
                        .OrderByDescending(image => image.VoteAverage)
                        .ThenBy(image => image.Id)
                        .Select(image => (int?)image.Id)
                        .FirstOrDefault(),
                })
                .ToListAsync(ct)
        ).ToDictionary(title => title.Id, title => (title.Poster, title.ImageId));
        Dictionary<int, (string? Path, int? Id)> moviePosters = (
            await context
                .Movies.AsNoTracking()
                .Where(movie => movieIds.Contains(movie.Id))
                .Select(movie => new
                {
                    movie.Id,
                    Poster = movie
                        .Images.Where(image => image.Type == PosterType && image.Iso6391 == null)
                        .OrderByDescending(image => image.VoteAverage)
                        .ThenBy(image => image.Id)
                        .Select(image => image.FilePath)
                        .FirstOrDefault(),
                    ImageId = movie
                        .Images.Where(image => image.Type == PosterType && image.Iso6391 == null)
                        .OrderByDescending(image => image.VoteAverage)
                        .ThenBy(image => image.Id)
                        .Select(image => (int?)image.Id)
                        .FirstOrDefault(),
                })
                .ToListAsync(ct)
        ).ToDictionary(title => title.Id, title => (title.Poster, title.ImageId));

        return
        [
            .. tvRows.Select(row =>
                row with
                {
                    TextlessPoster = tvPosters.GetValueOrDefault(row.ItemId).Path,
                    TextlessImageId = tvPosters.GetValueOrDefault(row.ItemId).Id,
                }
            ),
            .. movieRows.Select(row =>
                row with
                {
                    TextlessPoster = moviePosters.GetValueOrDefault(row.ItemId).Path,
                    TextlessImageId = moviePosters.GetValueOrDefault(row.ItemId).Id,
                    IsMovie = true,
                }
            ),
        ];
    }

    /// <summary>
    /// The posters of every group, each with its palette. The palettes are
    /// loaded after the pick, so only the posters a card shows are read.
    /// </summary>
    public static async Task<Dictionary<int, GroupPoster[]>> PickWithPalettesAsync(
        MediaContext context,
        List<GroupPosterRow> rows,
        CancellationToken ct
    )
    {
        Dictionary<int, GroupPosterRow[]> picked = Pick(rows);
        List<GroupPosterRow> shown = [.. picked.Values.SelectMany(group => group)];

        // A textless poster has a palette of its own (key "image"); a title's
        // own poster uses the title's palette (key "poster").
        List<int> imageIds = [.. shown.Select(row => row.TextlessImageId).OfType<int>().Distinct()];
        List<int> tvIds =
        [
            .. shown
                .Where(row => row.TextlessImageId is null && !row.IsMovie)
                .Select(row => row.ItemId)
                .Distinct(),
        ];
        List<int> movieIds =
        [
            .. shown
                .Where(row => row.TextlessImageId is null && row.IsMovie)
                .Select(row => row.ItemId)
                .Distinct(),
        ];

        Dictionary<int, PaletteColors?> imagePalettes =
            imageIds.Count == 0
                ? []
                : (
                    await context
                        .Images.AsNoTracking()
                        .Where(image => imageIds.Contains(image.Id))
                        .Select(image => new { image.Id, image._colorPalette })
                        .ToListAsync(ct)
                ).ToDictionary(
                    image => image.Id,
                    image => ColorPalette.FromJsonOrNull(image._colorPalette)?.Image
                );
        Dictionary<int, PaletteColors?> tvPalettes =
            tvIds.Count == 0
                ? []
                : (
                    await context
                        .Tvs.AsNoTracking()
                        .Where(tv => tvIds.Contains(tv.Id))
                        .Select(tv => new { tv.Id, tv._colorPalette })
                        .ToListAsync(ct)
                ).ToDictionary(
                    tv => tv.Id,
                    tv => ColorPalette.FromJsonOrNull(tv._colorPalette)?.Poster
                );
        Dictionary<int, PaletteColors?> moviePalettes =
            movieIds.Count == 0
                ? []
                : (
                    await context
                        .Movies.AsNoTracking()
                        .Where(movie => movieIds.Contains(movie.Id))
                        .Select(movie => new { movie.Id, movie._colorPalette })
                        .ToListAsync(ct)
                ).ToDictionary(
                    movie => movie.Id,
                    movie => ColorPalette.FromJsonOrNull(movie._colorPalette)?.Poster
                );

        return picked.ToDictionary(
            group => group.Key,
            group =>
                group
                    .Value.Select(
                        (row, index) =>
                        {
                            PaletteColors? palette =
                                row.TextlessImageId is { } imageId
                                    ? imagePalettes.GetValueOrDefault(imageId)
                                : row.IsMovie ? moviePalettes.GetValueOrDefault(row.ItemId)
                                : tvPalettes.GetValueOrDefault(row.ItemId);

                            return new GroupPoster(
                                row.Path!,
                                index < FrontPosters ? palette : null,
                                CardColor.Pick(palette)
                            );
                        }
                    )
                    .ToArray()
        );
    }

    /// <summary>
    /// Up to <see cref="Max"/> titles with a unique poster per group, first
    /// added title first. A new title only joins while the group has room, so
    /// once a group holds <see cref="Max"/> titles its posters stop changing.
    /// The rows hold only titles the user can play. A poster without text (no
    /// language) is preferred, because the cover tilts and crops it and
    /// printed titles turn into noise.
    /// </summary>
    public static Dictionary<int, GroupPosterRow[]> Pick(IEnumerable<GroupPosterRow> rows) =>
        rows.GroupBy(row => row.GroupId)
            .ToDictionary(
                group => group.Key,
                group =>
                    group
                        .OrderBy(row => row.AddedAt)
                        .ThenBy(row => row.TitleSort, StringComparer.Ordinal)
                        .ThenBy(row => row.ItemId)
                        .Where(row => !string.IsNullOrEmpty(row.Path))
                        .DistinctBy(row => row.Path)
                        .Take(Max)
                        .ToArray()
            );
}
