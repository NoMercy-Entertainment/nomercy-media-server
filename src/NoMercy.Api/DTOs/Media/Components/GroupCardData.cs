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
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.DTOs.Media.Components;

/// <summary>
/// Data for NMGroupCard: a group of titles with no image of its own (an anime
/// theme, demographic or season, a music genre or a playlist without a cover),
/// drawn from the images of the titles inside.
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
    public GroupPoster[] ItemPosters { get; set; } = [];

    /// <summary>The shape of the item images: "poster" (2:3) or "square" (album covers).</summary>
    [JsonProperty("item_shape")]
    public string ItemShape { get; set; } = PosterShape;

    [JsonProperty("display_title")]
    public GroupCardText DisplayTitle { get; set; } = GroupCardText.Of(string.Empty);

    [JsonProperty("subtitle")]
    public GroupCardText Subtitle { get; set; } = GroupCardText.Of(string.Empty);

    [JsonProperty("icon")]
    public string Icon { get; set; } = GroupCardIcons.DefaultIcon;

    [JsonProperty("accent_color")]
    public string AccentColor { get; set; } = NeutralAccentColor;

    [JsonProperty("overview")]
    public string? Overview { get; set; }

    private const string NeutralAccentColor = "#64748b";

    public const string PosterShape = "poster";
    public const string SquareShape = "square";

    public GroupCardData() { }

    /// <summary>
    /// A music genre or playlist without a cover of its own, drawn from the
    /// covers of the albums inside. Title and link stay what its music card
    /// sent, so navigation does not change.
    /// </summary>
    public GroupCardData(MusicCardData card, Guid id, GroupPoster[] itemPosters)
    {
        Id = NumericId(id);
        Title = card.Name;
        TitleSort = card.Name.TitleSort();
        Type = card.Type ?? string.Empty;
        Link = new(card.Link, UriKind.Relative);
        NumberOfItems = (int)(card.Tracks ?? 0);
        HaveItems = NumberOfItems;
        ItemPosters = MusicCover.Served(itemPosters);
        ItemShape = SquareShape;
        DisplayTitle = GroupCardText.Of(Title);
        Subtitle = new("group.items", new() { ["count"] = HaveItems });
        Icon = GroupCardIcons.For(Title);
        AccentColor = FrontColor(itemPosters);
    }

    public GroupCardData(AnimeThemeWithCountsDto dto)
        : this(new GenreCardData(dto), dto.ItemPosters)
    {
        Overview = dto.Description;
    }

    public GroupCardData(AnimeDemographicWithCountsDto dto)
        : this(new GenreCardData(dto), dto.ItemPosters) { }

    public GroupCardData(AnimeSeasonWithCountsDto dto)
        : this(new GenreCardData(dto), dto.ItemPosters) { }

    // Title, sort key, link and counts come from the one place that already
    // derives them for each group type.
    private GroupCardData(GenreCardData group, GroupPoster[] itemPosters)
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

        DisplayTitle =
            SeasonName(group.Quarter) is { } season && group.Year is { } year
                ? new($"group.season.{season}", new() { ["year"] = year })
                : GroupCardText.Of(Title);
        Subtitle = new("group.items", new() { ["count"] = HaveItems });
        Icon = GroupCardIcons.For(Title);
        AccentColor = FrontColor(itemPosters);
    }

    private static string FrontColor(GroupPoster[] itemPosters) =>
        itemPosters
            .Take(GroupItemPosters.FrontPosters)
            .Select(poster => poster.Color)
            .FirstOrDefault(color => !string.IsNullOrEmpty(color))
        ?? NeutralAccentColor;

    // Released clients decode the id as a 32-bit number, so a Guid-keyed group
    // sends a stable number taken from its Guid; the link carries the real id.
    private static int NumericId(Guid id) =>
        BitConverter.ToInt32(id.ToByteArray(), 0) & int.MaxValue;

    private static string? SeasonName(string? quarter) =>
        quarter?.ToLowerInvariant() switch
        {
            "winter" => "winter",
            "spring" => "spring",
            "summer" => "summer",
            "fall" => "fall",
            _ => null,
        };
}
