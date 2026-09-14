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

public record ComponentDto<T>
{
    public ComponentDto()
    {
        Id = Ulid.NewUlid();
        Update = new(Id) { When = null };
    }

    [JsonProperty("id")]
    public Ulid Id { get; set; }

    [JsonProperty("component")]
    public string Component { get; set; } = string.Empty;

    [JsonProperty("props")]
    public RenderProps<T> Props { get; set; } = new();

    [JsonProperty("update")]
    public Update Update { get; set; }

    [JsonProperty("replacing")]
    public Ulid Replacing { get; set; }
}
