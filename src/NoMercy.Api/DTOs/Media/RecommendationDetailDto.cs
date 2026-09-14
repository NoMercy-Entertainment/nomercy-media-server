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

public record RecommendationDetailDto
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("title")]
    public string? Title { get; set; }

    [JsonProperty("overview")]
    public string? Overview { get; set; }

    [JsonProperty("poster")]
    public string? Poster { get; set; }

    [JsonProperty("backdrop")]
    public string? Backdrop { get; set; }

    [JsonProperty("logo")]
    public string? Logo { get; set; }

    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    [JsonProperty("media_type")]
    public string MediaType { get; set; } = string.Empty;

    [JsonProperty("year")]
    public int? Year { get; set; }

    [JsonProperty("voteAverage")]
    public double? VoteAverage { get; set; }

    [JsonProperty("genres")]
    public IEnumerable<GenreDto> Genres { get; set; } = [];

    [JsonProperty("content_ratings")]
    public IEnumerable<ContentRating> ContentRatings { get; set; } = [];

    [JsonProperty("external_ids")]
    public ExternalIds? ExternalIds { get; set; }

    [JsonProperty("because_you_have")]
    public List<RecommendationDetailSourceDto> BecauseYouHave { get; set; } = [];

    [JsonProperty("link")]
    public Uri Link => new($"/{MediaType}/{Id}", UriKind.Relative);
}
