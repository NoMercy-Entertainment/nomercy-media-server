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
