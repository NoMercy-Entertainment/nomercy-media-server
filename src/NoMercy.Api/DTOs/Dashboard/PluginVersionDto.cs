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

public class PluginVersionDto(PluginVersionEntry entry)
{
    [JsonProperty("version")]
    public string Version { get; } = entry.Version;

    [JsonProperty("target_abi")]
    public string? TargetAbi { get; } = entry.TargetAbi;

    [JsonProperty("changelog")]
    public string? Changelog { get; } = entry.Changelog;

    [JsonProperty("timestamp")]
    public DateTime? Timestamp { get; } = entry.Timestamp;

    /// <summary>
    /// Whether the repository published a checksum for this version. The URL
    /// itself never leaves the server: the dashboard names a version and the
    /// server decides what it fetches, so a client cannot point an install at
    /// something the catalogue does not list.
    /// </summary>
    [JsonProperty("verified")]
    public bool Verified { get; } = !string.IsNullOrWhiteSpace(entry.Checksum);
}
