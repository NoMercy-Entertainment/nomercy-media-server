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

/// <summary>Whether this server runs from imported bundles, and for how long one counts.</summary>
public record PluginOfflineModeDto
{
    [JsonProperty("enabled")]
    public bool Enabled { get; init; }

    /// <summary>
    /// How many days a bundle stays good for. The bundle carries its own
    /// number and that one decides; this is what the owner asks NoMercy to
    /// issue next time.
    /// </summary>
    [JsonProperty("bundleValidityDays")]
    public int BundleValidityDays { get; init; }
}
