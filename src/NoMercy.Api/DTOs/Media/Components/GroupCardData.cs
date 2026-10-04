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

using Newtonsoft.Json;
using NoMercy.Data.Repositories;

namespace NoMercy.Api.DTOs.Media.Components;

/// <summary>
/// Data for NMGroupCard: a group of titles with no image of its own (an anime
/// theme, demographic or season), drawn from the posters of the titles inside.
/// Colors are not sent: the app takes them from the posters, so the same
/// images serve every other place they appear.
/// </summary>
public record GroupCardData
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonProperty("titleSort")]
    public string TitleSort { get; set; } = string.Empty;

    [JsonProperty("type")]
    public string Type { get; set; } = string.Empty;

    [JsonProperty("link")]
    public Uri Link { get; set; } = null!;

    // A season's label is formatted by the app in its own locale.
    [JsonProperty("year")]
    public int? Year { get; set; }

    [JsonProperty("quarter")]
    public string? Quarter { get; set; }

    [JsonProperty("number_of_items")]
    public int NumberOfItems { get; set; }

    [JsonProperty("have_items")]
    public int HaveItems { get; set; }

    [JsonProperty("item_posters")]
    public string[] ItemPosters { get; set; } = [];

    public GroupCardData() { }

    public GroupCardData(AnimeThemeWithCountsDto dto)
        : this(new GenreCardData(dto), dto.ItemPosters) { }

    public GroupCardData(AnimeDemographicWithCountsDto dto)
        : this(new GenreCardData(dto), dto.ItemPosters) { }

    public GroupCardData(AnimeSeasonWithCountsDto dto)
        : this(new GenreCardData(dto), dto.ItemPosters) { }

    // Title, sort key, link and counts come from the one place that already
    // derives them for each group type.
    private GroupCardData(GenreCardData group, string[] itemPosters)
    {
        Id = (int)group.Id!;
        Title = group.Title ?? string.Empty;
        TitleSort = group.TitleSort ?? string.Empty;
        Type = group.Type ?? string.Empty;
        Link = group.Link;
        Year = group.Year;
        Quarter = group.Quarter;
        NumberOfItems = group.NumberOfItems ?? 0;
        HaveItems = group.HaveItems ?? 0;
        ItemPosters = itemPosters;
    }
}
