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

using NoMercy.Api.DTOs.Media.Components;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.Services;

/// <summary>How a lolomo of groups is split into rows once it is too long for one.</summary>
public enum AnimeGroupRows
{
    /// <summary>One row per first letter, # first, then A to Z.</summary>
    Letter,

    /// <summary>One row per year, newest first, quarters in season order.</summary>
    YearDescending,
}

/// <summary>
/// The list page of anime groups (themes, demographics, seasons): one grid, or
/// for a TV lolomo the rows its hero and carousels draw.
/// </summary>
public static class AnimeGroupPage
{
    /// <summary>
    /// The request header an app lists the components it can draw in, comma
    /// separated. An app that does not send it gets only the components every
    /// released app knows, so an old build never meets a type it cannot decode.
    /// </summary>
    public const string ComponentsHeader = "NM-Components";

    /// <summary>A row holds at most this many cards; a longer page splits into rows.</summary>
    public const int RowMax = 20;

    public static bool Draws(string? header, string component) =>
        !string.IsNullOrEmpty(header)
        && header
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Contains(component, StringComparer.Ordinal);

    /// <param name="groups">The groups to show, already limited to those the user can play.</param>
    /// <param name="lolomo">
    /// The app asked for rows. Honored only together with <paramref name="groupCard"/>:
    /// released TV apps already send it and take their hero from the first carousel
    /// item as an NMCard, so rows of NMGenreCard would give them a blank hero.
    /// </param>
    /// <param name="groupCard">The app draws NMGroupCard; otherwise it gets today's grid of NMGenreCard.</param>
    public static ComponentResponse Build(
        string id,
        IEnumerable<(GenreCardData Card, string[] Posters)> groups,
        AnimeGroupRows rows,
        bool lolomo,
        bool groupCard
    )
    {
        List<(GenreCardData Card, string[] Posters)> list = [.. groups];

        if (!lolomo || !groupCard)
            return ComponentResponse.From(
                Component.Grid().WithId(id).WithItems(list.Select(Card)).Build()
            );

        if (list.Count <= RowMax)
            return ComponentResponse.From(
                Component.Carousel().WithId(id).WithItems(list.Select(Card)).Build()
            );

        List<IGrouping<string, (GenreCardData Card, string[] Posters)>> split =
            rows == AnimeGroupRows.Letter
                ?
                [
                    .. list.GroupBy(group => AlphaBucket.LetterFor(group.Card.TitleSort))
                        .OrderBy(row => Array.IndexOf(AlphaBucket.Buckets, row.Key)),
                ]
                :
                [
                    .. list.GroupBy(group => group.Card.Year?.ToString("D4") ?? "#")
                        .OrderByDescending(row => row.Key, StringComparer.Ordinal),
                ];

        return new()
        {
            Data =
            [
                .. split.Select(
                    (row, index) =>
                        Component
                            .Carousel()
                            .WithId(row.Key)
                            .WithTitle(row.Key)
                            .WithNavigation(
                                index == 0 ? null : split[index - 1].Key,
                                index == split.Count - 1 ? null : split[index + 1].Key
                            )
                            .WithItems(
                                row.OrderBy(group => group.Card.TitleSort, StringComparer.Ordinal)
                                    .Select(Card)
                            )
                            .Build()
                ),
            ],
        };

        IComponentBuilder Card((GenreCardData Card, string[] Posters) group) =>
            groupCard
                ? Component.GroupCard().WithData(new GroupCardData(group.Card, group.Posters))
                : Component.GenreCard().WithData(group.Card);
    }
}
