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
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Database;
using NoMercy.Database.Models.Music;
using NoMercy.NmSystem.Information;

namespace NoMercy.Api.DTOs.Music;

public record ReleaseGroupDto
{
    [JsonProperty("id")]
    public Guid Id { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; }

    [JsonProperty("description")]
    public string? Description { get; set; }

    [JsonProperty("cover")]
    public string? Cover { get; set; }

    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    [JsonProperty("library_id")]
    public Ulid? LibraryId { get; set; }

    [JsonProperty("origin")]
    public Guid Origin { get; set; }

    [JsonProperty("type")]
    public string Type { get; set; }

    [JsonProperty("year")]
    public int Year { get; set; }

    [JsonProperty("link")]
    public Uri Link { get; set; }

    public ReleaseGroupDto(AlbumReleaseGroup artistReleaseGroup, string country)
    {
        string? description = artistReleaseGroup
            .ReleaseGroup.Translations.FirstOrDefault(translation =>
                translation.Iso31661 == country
            )
            ?.Description;

        Description = !string.IsNullOrEmpty(description)
            ? description
            : artistReleaseGroup.ReleaseGroup.Description;

        Id = artistReleaseGroup.ReleaseGroupId;
        Title = artistReleaseGroup.ReleaseGroup.Title;
        Cover = artistReleaseGroup.ReleaseGroup.Cover;
        Cover = MusicCover.UrlWhenSet(Cover);
        ColorPalette = artistReleaseGroup.ReleaseGroup.ColorPalette;
        LibraryId = artistReleaseGroup.ReleaseGroup.LibraryId;
        Origin = Info.DeviceId;
        Type = "release_groups";
        Year = artistReleaseGroup.ReleaseGroup.Year;
        Link = new($"/music/release_groups/{Id}", UriKind.Relative);
    }
}
