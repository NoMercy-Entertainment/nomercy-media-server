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
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Api.DTOs.Dashboard;

public class PluginRepositoryInfoDto(PluginRepositoryInfo info)
{
    [JsonProperty("name")]
    public string Name { get; } = info.Name;

    [JsonProperty("url")]
    public string Url { get; } = info.Url;

    [JsonProperty("enabled")]
    public bool Enabled { get; } = info.Enabled;

    /// <summary>
    /// Whether the owner trusts where these plugins come from. The strongest
    /// thing they say about a repository, and it was in no response at all.
    /// </summary>
    [JsonProperty("trusted")]
    public bool Trusted { get; } = info.Trusted;
}
