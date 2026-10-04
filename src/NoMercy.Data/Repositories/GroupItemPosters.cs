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

namespace NoMercy.Data.Repositories;

/// <summary>
/// One title inside a group (an anime theme, demographic or season), as the
/// group card's poster mosaic needs it.
/// </summary>
public record GroupPosterRow(
    int GroupId,
    int ItemId,
    DateTime AddedAt,
    string TitleSort,
    string? TextlessPoster,
    string? Poster
);

/// <summary>
/// Picks the posters a group card shows for a group that has no image of its
/// own: the posters of the titles inside it.
/// </summary>
public static class GroupItemPosters
{
    public const int Max = 9;

    public const string PosterType = "poster";

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
        Dictionary<int, string?> tvPosters = await context
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
            })
            .ToDictionaryAsync(title => title.Id, title => title.Poster, ct);
        Dictionary<int, string?> moviePosters = await context
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
            })
            .ToDictionaryAsync(title => title.Id, title => title.Poster, ct);

        return
        [
            .. tvRows.Select(row =>
                row with
                {
                    TextlessPoster = tvPosters.GetValueOrDefault(row.ItemId),
                }
            ),
            .. movieRows.Select(row =>
                row with
                {
                    TextlessPoster = moviePosters.GetValueOrDefault(row.ItemId),
                }
            ),
        ];
    }

    /// <summary>
    /// Up to <see cref="Max"/> unique posters per group, first added title
    /// first. A new title only joins while the group has room, so once a group
    /// holds <see cref="Max"/> titles its posters stop changing. The rows hold
    /// only titles the user can play. A poster without text (no language) is
    /// preferred, because the cover tilts and crops it and printed titles turn
    /// into noise.
    /// </summary>
    public static Dictionary<int, string[]> Pick(IEnumerable<GroupPosterRow> rows) =>
        rows.GroupBy(row => row.GroupId)
            .ToDictionary(
                group => group.Key,
                group =>
                    group
                        .OrderBy(row => row.AddedAt)
                        .ThenBy(row => row.TitleSort, StringComparer.Ordinal)
                        .ThenBy(row => row.ItemId)
                        .Select(row => row.TextlessPoster ?? row.Poster)
                        .OfType<string>()
                        .Where(path => path.Length > 0)
                        .Distinct()
                        .Take(Max)
                        .ToArray()
            );
}
