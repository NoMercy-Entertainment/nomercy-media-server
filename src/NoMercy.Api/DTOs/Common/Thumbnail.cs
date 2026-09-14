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

using Mono.Nat;
using Newtonsoft.Json;
using NoMercy.Providers.Helpers;

namespace NoMercy.Api.DTOs.Common;

public class Thumbnail
{
    [JsonProperty("url")]
    public Uri? Url { get; set; }

    [JsonProperty("preference")]
    public long Preference { get; set; }

    [JsonProperty("id")]
    [JsonConverter(typeof(ParseStringConverter))]
    public long Id { get; set; }

    [JsonProperty("height", NullValueHandling = NullValueHandling.Ignore)]
    public long? Height { get; set; }

    [JsonProperty("width", NullValueHandling = NullValueHandling.Ignore)]
    public long? Width { get; set; }

    [JsonProperty("resolution", NullValueHandling = NullValueHandling.Ignore)]
    public string? Resolution { get; set; }
}
