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

/// <summary>
/// One capability a plugin asks for, and where the owner has got to with it.
/// </summary>
public class PluginCapabilityStateDto
{
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>A translation key the client resolves, never a sentence.</summary>
    [JsonProperty("summary_key")]
    public string SummaryKey { get; set; } = string.Empty;

    [JsonProperty("trust")]
    public string Trust { get; set; } = string.Empty;

    [JsonProperty("docs_url")]
    public string DocsUrl { get; set; } = string.Empty;

    [JsonProperty("approved")]
    public bool Approved { get; set; }

    /// <summary>
    /// The plugin version the owner approved this at, or null when they have
    /// not. A client shows "approved for 1.0.0, this is 2.0.0" from these two.
    /// </summary>
    [JsonProperty("approved_at_version")]
    public string? ApprovedAtVersion { get; set; }

    /// <summary>What the plugin said it needs it for, as a key the plugin ships.</summary>
    [JsonProperty("reason_key")]
    public string? ReasonKey { get; set; }
}
