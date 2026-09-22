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

/// <summary>One plugin the catalogue offers, told against what is installed.</summary>
public class PluginCatalogueEntryDto
{
    [JsonProperty("id")]
    public Ulid Id { get; init; }

    [JsonProperty("name")]
    public string Name { get; init; } = null!;

    [JsonProperty("description")]
    public string Description { get; init; } = null!;

    [JsonProperty("author")]
    public string? Author { get; init; }

    [JsonProperty("project_url")]
    public string? ProjectUrl { get; init; }

    [JsonProperty("versions")]
    public IReadOnlyList<PluginVersionDto> Versions { get; init; } = [];

    /// <summary>The newest version the catalogue carries, or null when it carries none.</summary>
    [JsonProperty("latest_version")]
    public string? LatestVersion { get; init; }

    /// <summary>What is on this server right now, or null when nothing is.</summary>
    [JsonProperty("installed_version")]
    public string? InstalledVersion { get; init; }

    /// <summary>
    /// Installed, and the catalogue has something newer. Answered here rather
    /// than in each client: "newer" is a version comparison, and three clients
    /// comparing version strings by hand is three different answers.
    /// </summary>
    [JsonProperty("update_available")]
    public bool UpdateAvailable { get; init; }
}
