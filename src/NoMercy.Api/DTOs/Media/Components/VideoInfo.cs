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
using NoMercy.Database;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.DTOs.Media.Components;

public record VideoInfo
{
    [JsonProperty("id")]
    public string? Id { get; set; }

    [JsonProperty("name")]
    public string? Name { get; set; }

    [JsonProperty("site")]
    public string? Site { get; set; }

    [JsonProperty("type")]
    public string? Type { get; set; }
}
