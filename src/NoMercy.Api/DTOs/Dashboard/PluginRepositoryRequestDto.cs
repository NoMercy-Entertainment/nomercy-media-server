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

public class PluginRepositoryRequestDto
{
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("url")]
    public string Url { get; set; } = string.Empty;
}

/// <summary>Whether the owner trusts a repository they already added.</summary>
public class PluginRepositoryTrustRequestDto
{
    [JsonProperty("trusted")]
    public bool Trusted { get; set; }
}
