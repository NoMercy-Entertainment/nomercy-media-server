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

using System.Text.Json.Serialization;

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>One plugin this plugin needs, by id and semver range. Free depends only on free.</summary>
public sealed record PluginDependency(
    [property: JsonPropertyName("id")] PluginId Id,
    [property: JsonPropertyName("range")] string Range,
    [property: JsonPropertyName("tier")] PluginTier Tier
);
