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

namespace NoMercy.Plugins.Abstractions;

/// <summary>
/// What a caller needs to open one route of a plugin.
///
/// Coarser than PluginAccess on purpose: a plugin is owned, shared or out of
/// reach, and within a plugin somebody can see a route only the owner may use.
/// The server hides an owner route from a member before the client sees it,
/// because offering a page that answers 403 is worse than not offering it.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PluginRouteAccess>))]
public enum PluginRouteAccess
{
    [JsonStringEnumMemberName("shared")]
    Shared,

    [JsonStringEnumMemberName("owner")]
    Owner,
}
