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

/// <summary>
/// One button in a <see cref="PluginTableCellType.Actions"/> cell. The row prop
/// named by the column's key holds the list of them, so a row can offer a pause
/// and a destructive cancel without the plugin drawing a second list under the
/// table.
/// </summary>
public class PluginTableAction
{
    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("icon")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Icon { get; init; }

    /// <summary>"danger" draws the destructive button. Null draws the plain one.</summary>
    [JsonPropertyName("variant")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Variant { get; init; }

    [JsonPropertyName("action")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PluginActionIntent? Action { get; init; }

    // Newtonsoft writes every response on this path and reads its own attribute,
    // so the three above would go out as explicit nulls without these.
    public bool ShouldSerializeIcon() => Icon is not null;

    public bool ShouldSerializeVariant() => Variant is not null;

    public bool ShouldSerializeAction() => Action is not null;
}
