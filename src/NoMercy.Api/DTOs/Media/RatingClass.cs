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
using NoMercy.Database.Models.Common;

namespace NoMercy.Api.DTOs.Media;

public record RatingClass
{
    [JsonProperty("rating")]
    public string? Rating { get; set; } = string.Empty;

    [JsonProperty("meaning")]
    public string Meaning { get; set; } = string.Empty;

    [JsonProperty("order")]
    public long Order { get; set; }

    [JsonProperty("iso_3166_1")]
    public string? Iso31661 { get; set; } = string.Empty;

    [JsonProperty("image")]
    public string Image { get; set; } = string.Empty;

    /// <summary>A rating is shown when it is the US rating or the viewer's own country's.</summary>
    public static bool IsShownIn(Certification certification, string? country) =>
        certification.Iso31661 == "US" || certification.Iso31661 == country;

    public static RatingClass From(Certification certification) =>
        new()
        {
            Rating = certification.Rating,
            Iso31661 = certification.Iso31661,
            Image =
                $"/{certification.Iso31661}/{certification.Iso31661}_{certification.Rating}.svg",
        };
}
