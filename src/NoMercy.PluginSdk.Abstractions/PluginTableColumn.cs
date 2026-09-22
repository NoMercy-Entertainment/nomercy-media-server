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
/// One column of a <see cref="PluginComponentType.Table"/>. A row supplies its
/// cells by <see cref="Key"/>, so column order is presentation and never
/// something a row has to agree with positionally.
/// </summary>
public class PluginTableColumn
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }

    /// <summary>
    /// How the cell renders: plain text unless the column says otherwise.
    /// Progress and badge cells let a table carry the two things a list of
    /// ongoing operations always needs without nesting components per cell.
    /// </summary>
    [JsonPropertyName("cell")]
    public string Cell { get; init; } = PluginTableCellType.Text;

    [JsonPropertyName("align")]
    public string? Align { get; init; }

    /// <summary>A CSS-ish width hint. Advisory; a client may ignore it.</summary>
    [JsonPropertyName("width")]
    public string? Width { get; init; }
}
