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

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoMercy.Plugins.Ipc;

/// <summary>
/// How every payload on the channel is written and read.
/// <para>
/// One set of options for both sides. Two sets drift, and a payload written
/// with one and read with the other fails at a boundary where the only
/// evidence is a plugin that stopped working.
/// </para>
/// <para>
/// Enums cross by name, not by number. A plugin is built against the SDK
/// version it pinned and the server ships its own, so the day the contract
/// adds a member in the middle of an enum, every number after it means
/// something else on one side of the channel. The name keeps meaning the same
/// thing.
/// </para>
/// </summary>
public static class PluginWireJson
{
    public static JsonSerializerOptions Options { get; } =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
}
