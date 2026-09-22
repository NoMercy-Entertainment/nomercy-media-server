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

/// <summary>One input in a <see cref="PluginComponentType.Form"/>.</summary>
public class PluginFormField
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = PluginFormFieldType.Text;

    [JsonPropertyName("value")]
    public object? Value { get; init; }

    [JsonPropertyName("placeholder")]
    public string? Placeholder { get; init; }

    [JsonPropertyName("required")]
    public bool Required { get; init; }

    /// <summary>Options for <see cref="PluginFormFieldType.Select"/>.</summary>
    [JsonPropertyName("options")]
    public List<PluginFormOption> Options { get; init; } = [];

    /// <summary>
    /// Accepted extensions or MIME types for a
    /// <see cref="PluginFormFieldType.File"/> field, e.g. <c>.torrent</c>.
    /// </summary>
    [JsonPropertyName("accept")]
    public string? Accept { get; init; }

    [JsonPropertyName("multiple")]
    public bool Multiple { get; init; }
}
