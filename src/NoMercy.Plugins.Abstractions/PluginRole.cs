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

[JsonConverter(typeof(JsonStringEnumConverter<PluginRole>))]
public enum PluginRole
{
    [JsonStringEnumMemberName("owner")]
    Owner,

    [JsonStringEnumMemberName("manager")]
    Manager,

    [JsonStringEnumMemberName("member")]
    Member,

    [JsonStringEnumMemberName("guest")]
    Guest,
}
