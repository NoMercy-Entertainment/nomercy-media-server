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

/// <summary>A group the user can see: its name, and its year and quarter for a season.</summary>
public record GroupHead(int Id, string Title, int Year = 0, string Quarter = "");

/// <summary>A group-to-movie link of a playable movie, with the date it was added.</summary>
public record GroupMovieLink(int GroupId, int MovieId, DateTime AddedAt);

/// <summary>
/// Builds the rows <see cref="AnimeGroupRanking"/> orders, without posters: the
/// "which groups show first" query must stay light, because it covers every
/// group and only the few groups it picks load posters.
/// </summary>
public static class AnimeGroupRankingQueries
{
    /// <summary>
    /// A show is watched when this user has a watch record for it. Only playable
    /// titles count, in the group size and in the watched count.
    /// </summary>
    public static async Task<List<GroupRankRow>> RankRowsAsync(
        MediaContext context,
        Guid userId,
        List<GroupHead> heads,
        List<GroupLink> tvLinks,
        List<GroupMovieLink> movieLinks,
        CancellationToken ct
    )
    {
        List<int> tvIds = [.. tvLinks.Select(link => link.TvId).Distinct()];
        Dictionary<int, DateTime> playableTvs =
            tvIds.Count == 0
                ? []
                : await context
                    .Tvs.AsNoTracking()
                    .Where(tv =>
                        tvIds.Contains(tv.Id)
                        && tv.Episodes.Any(e => e.VideoFiles.Any(v => v.Folder != null))
                    )
                    .Select(tv => new { tv.Id, tv.CreatedAt })
                    .ToDictionaryAsync(tv => tv.Id, tv => tv.CreatedAt, ct);

        HashSet<int> watchedTvIds =
        [
            .. await context
                .UserData.AsNoTracking()
                .Where(data => data.UserId == userId && data.TvId != null)
                .Select(data => data.TvId!.Value)
                .Distinct()
                .ToListAsync(ct),
        ];
        HashSet<int> watchedMovieIds =
        [
            .. await context
                .UserData.AsNoTracking()
                .Where(data => data.UserId == userId && data.MovieId != null)
                .Select(data => data.MovieId!.Value)
                .Distinct()
                .ToListAsync(ct),
        ];

        ILookup<int, GroupLink> tvByGroup = tvLinks
            .Where(link => playableTvs.ContainsKey(link.TvId))
            .ToLookup(link => link.GroupId);
        ILookup<int, GroupMovieLink> movieByGroup = movieLinks.ToLookup(link => link.GroupId);

        return
        [
            .. heads.Select(head =>
            {
                List<GroupLink> tvs = [.. tvByGroup[head.Id]];
                List<GroupMovieLink> movies = [.. movieByGroup[head.Id]];

                IEnumerable<DateTime> added = tvs.Select(link => playableTvs[link.TvId])
                    .Concat(movies.Select(link => link.AddedAt));

                return new GroupRankRow(
                    head.Id,
                    head.Title,
                    tvs.Count + movies.Count,
                    added.Select(date => (DateTime?)date).Max(),
                    tvs.Count(link => watchedTvIds.Contains(link.TvId))
                        + movies.Count(link => watchedMovieIds.Contains(link.MovieId)),
                    head.Year,
                    QuarterOrder(head.Quarter)
                );
            }),
        ];
    }

    private static int QuarterOrder(string quarter) =>
        quarter switch
        {
            "WINTER" => 1,
            "SPRING" => 2,
            "SUMMER" => 3,
            "FALL" => 4,
            _ => 0,
        };
}
