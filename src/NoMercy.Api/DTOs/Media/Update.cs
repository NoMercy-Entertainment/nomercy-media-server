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
using NoMercy.Api.DTOs.Music;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.DTOs.Media;

public record Update
{
    [JsonProperty("when")]
    public string? When { get; set; }

    [JsonProperty("link")]
    public Uri Link { get; set; } = default!;

    [JsonProperty("body")]
    public object Body { get; set; } = new();

    public Update(Ulid ulid)
    {
        Body = new { replace_id = ulid };
    }
}
