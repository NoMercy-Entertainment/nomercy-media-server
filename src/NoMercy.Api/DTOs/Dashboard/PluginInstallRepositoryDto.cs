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

namespace NoMercy.Api.DTOs.Dashboard;

/// <summary>The repository an installed plugin was downloaded from.</summary>
public class PluginInstallRepositoryDto
{
    /// <summary>The name the repository had when the plugin was installed.</summary>
    [JsonProperty("name")]
    public string? Name { get; init; }

    /// <summary>The index URL the entry was read from.</summary>
    [JsonProperty("url")]
    public string Url { get; init; } = string.Empty;

    /// <summary>
    /// Whether this server trusts the repository now. Read on every request,
    /// not at install, so it follows the owner's current choice.
    /// </summary>
    [JsonProperty("trusted")]
    public bool Trusted { get; init; }

    /// <summary>Whether the repository was removed from this server since. A removed one is not trusted.</summary>
    [JsonProperty("removed")]
    public bool Removed { get; init; }
}
