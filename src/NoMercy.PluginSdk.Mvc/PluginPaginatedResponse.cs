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

namespace NoMercy.PluginSdk.Mvc;

/// <summary>The <c>{ data, next_page, has_more }</c> envelope.</summary>
public record PluginPaginatedResponse<T>
{
    [JsonProperty("data")]
    public IEnumerable<T> Data { get; set; } = [];

    [JsonProperty("next_page")]
    public int? NextPage { get; set; }

    [JsonProperty("has_more")]
    public bool HasMore { get; set; }
}
