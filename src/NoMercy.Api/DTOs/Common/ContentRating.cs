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

namespace NoMercy.Api.DTOs.Common;

public record ContentRating
{
    [JsonProperty("rating")]
    public string? Rating { get; set; }

    [JsonProperty("iso_3166_1")]
    public string? Iso31661 { get; set; }

    public static ContentRating From(Certification certification) =>
        new() { Rating = certification.Rating, Iso31661 = certification.Iso31661 };
}
