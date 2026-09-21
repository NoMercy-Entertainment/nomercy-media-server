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
/// What a caller may do with a plugin.
///
/// The server answers this once per user per plugin and every client obeys it.
/// A caller with none never reaches the plugin: the request is refused before
/// dispatch, so the client filter is the second line rather than the first.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PluginAccess>))]
public enum PluginAccess
{
    [JsonStringEnumMemberName("owned")]
    Owned,

    [JsonStringEnumMemberName("shared")]
    Shared,

    /// <summary>
    /// Not visible to this account. A listing drops the entry and a page
    /// answers 403, so nobody is offered something that will refuse them.
    /// </summary>
    [JsonStringEnumMemberName("none")]
    None,
}
