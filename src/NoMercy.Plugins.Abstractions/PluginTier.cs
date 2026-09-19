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

/// <summary>Informational only. The marketplace is authoritative about what a plugin costs.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PluginTier>))]
public enum PluginTier
{
    [JsonStringEnumMemberName("free")]
    Free,

    [JsonStringEnumMemberName("paid")]
    Paid,
}
