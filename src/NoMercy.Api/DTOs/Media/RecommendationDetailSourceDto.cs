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
using NoMercy.Api.DTOs.Common;
using NoMercy.Database;

namespace NoMercy.Api.DTOs.Media;

public record RecommendationDetailSourceDto
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("title")]
    public string? Title { get; set; }

    [JsonProperty("titleSort")]
    public string? TitleSort { get; set; }

    [JsonProperty("poster")]
    public string? Poster { get; set; }

    [JsonProperty("backdrop")]
    public string? Backdrop { get; set; }

    [JsonProperty("logo")]
    public string? Logo { get; set; }

    [JsonProperty("overview")]
    public string? Overview { get; set; }

    [JsonProperty("year")]
    public int? Year { get; set; }

    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    [JsonProperty("link")]
    public Uri Link => new($"/{MediaType}/{Id}", UriKind.Relative);

    [JsonProperty("media_type")]
    public string MediaType { get; set; } = string.Empty;

    [JsonProperty("have_items")]
    public int HaveItems { get; set; }

    [JsonProperty("number_of_items")]
    public int NumberOfItems { get; set; }

    [JsonProperty("duration")]
    public int? Duration { get; set; }

    [JsonProperty("tags")]
    public IEnumerable<string> Tags { get; set; } = [];
}
