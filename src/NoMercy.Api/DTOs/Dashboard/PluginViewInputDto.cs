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

namespace NoMercy.Api.DTOs.Dashboard;

/// <summary>
/// What a person typed on a plugin's screen, and which button they pressed.
/// <para>
/// Untrusted by definition: these values reach plugin code. The server does
/// not interpret them, which is why they are carried as JSON rather than
/// coerced here into types the plugin never asked for.
/// </para>
/// </summary>
public record PluginViewInputDto
{
    [JsonPropertyName("values")]
    public Dictionary<string, object?> Values { get; init; } = [];

    [JsonPropertyName("action")]
    public string? Action { get; init; }
}
