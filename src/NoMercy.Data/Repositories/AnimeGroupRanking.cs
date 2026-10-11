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
using System.Text;

namespace NoMercy.Data.Repositories;

/// <summary>Picks which anime groups a carousel shows, and in which order.</summary>
public static class AnimeGroupRanking
{
    public static List<int> TopIds(
        IEnumerable<GroupRankRow> rows,
        AnimeCarouselOrder order,
        AnimeGroupKind kind,
        DateOnly today,
        int take
    )
    {
        IEnumerable<GroupRankRow> playable = rows.Where(row => row.PlayableCount > 0);

        IEnumerable<GroupRankRow> ordered = order switch
        {
            AnimeCarouselOrder.Newest when kind == AnimeGroupKind.Season => playable
                .OrderByDescending(row => row.Year)
                .ThenByDescending(row => row.QuarterOrder)
                .ThenBy(row => row.Id),
            AnimeCarouselOrder.Newest => playable
                .OrderByDescending(row => row.NewestAddedAt)
                .ThenBy(row => row.Id),
            AnimeCarouselOrder.MostTitles => ByMostTitles(playable),
            AnimeCarouselOrder.MostWatched => ByMostTitles(
                playable.OrderByDescending(row => row.WatchedCount)
            ),
            _ => playable.OrderBy(row => DailyHash(kind, today, row.Id)).ThenBy(row => row.Id),
        };

        return [.. ordered.Take(take).Select(row => row.Id)];
    }

    private static IOrderedEnumerable<GroupRankRow> ByMostTitles(IEnumerable<GroupRankRow> rows) =>
        rows.OrderByDescending(row => row.PlayableCount)
            .ThenBy(row => row.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Id);

    private static IOrderedEnumerable<GroupRankRow> ByMostTitles(
        IOrderedEnumerable<GroupRankRow> rows
    ) =>
        rows.ThenByDescending(row => row.PlayableCount)
            .ThenBy(row => row.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Id);

    // FNV-1a over the text: string.GetHashCode changes per process, and the
    // order must be the same all day, also after a restart.
    private static uint DailyHash(AnimeGroupKind kind, DateOnly today, int id)
    {
        uint hash = 2166136261;
        foreach (byte value in Encoding.UTF8.GetBytes($"{kind}:{today:yyyyMMdd}:{id}"))
        {
            hash ^= value;
            hash *= 16777619;
        }
        return hash;
    }
}
