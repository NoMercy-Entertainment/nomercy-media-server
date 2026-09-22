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

namespace NoMercy.PluginSdk.Telemetry;

/// <summary>
/// Everything this server says about its plugins, and nothing else.
/// <para>
/// There is no member in here for a person, a title, a path or a library. What
/// leaves is which plugin refused what, how often, and on an owner's say-so
/// how often it crashed. That is the whole shape, so anyone can read it and
/// see what they are agreeing to.
/// </para>
/// </summary>
public record PluginTelemetryReport(
    [property: JsonPropertyName("server_id")] Guid ServerId,
    [property: JsonPropertyName("window_start")] DateTimeOffset WindowStart,
    [property: JsonPropertyName("window_end")] DateTimeOffset WindowEnd,
    [property: JsonPropertyName("plugins")] IReadOnlyList<PluginTelemetryEntry> Plugins
);

/// <summary>One plugin's window. Null counters are the owner not having opted in.</summary>
public record PluginTelemetryEntry(
    [property: JsonPropertyName("plugin_id")] Ulid PluginId,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("refusals")] IReadOnlyList<PluginTelemetryRefusal> Refusals,
    [property: JsonPropertyName("crashes")] int? Crashes,
    [property: JsonPropertyName("ceiling_hits")] int? CeilingHits
);

/// <summary>How often one refusal fired. The code, never what was being done.</summary>
public record PluginTelemetryRefusal(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("total")] int Total
);

/// <summary>A plugin arriving, changing or leaving. Never sent for a sideload.</summary>
public record PluginInstallReport(
    [property: JsonPropertyName("plugin_id")] Ulid PluginId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("event")] string Event
);
