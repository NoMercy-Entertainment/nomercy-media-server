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

public record RecommendationDto
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("title")]
    public string? Title { get; set; }

    [JsonProperty("overview")]
    public string? Overview { get; set; }

    [JsonProperty("poster")]
    public string? Poster { get; set; }

    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    [JsonProperty("type")]
    public string Type { get; set; } = string.Empty;

    [JsonProperty("link")]
    public Uri Link =>
        new(
            $"/dashboard/recommendations/{(Type != "movie" ? "tv" : "movie")}/{Id}",
            UriKind.Relative
        );

    // Internal properties used for scoring/diversity — not serialized to JSON
    [JsonIgnore]
    public string? TitleSort { get; set; }

    [JsonIgnore]
    public string? Backdrop { get; set; }

    [JsonIgnore]
    public double Score { get; set; }

    [JsonIgnore]
    public int SourceCount { get; set; }

    [JsonIgnore]
    public List<int> SourceIds { get; set; } = [];
}
