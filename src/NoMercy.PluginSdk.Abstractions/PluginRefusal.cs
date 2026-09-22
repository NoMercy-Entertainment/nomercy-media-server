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
/// What was blocked, why, and how to fix it, in three plain sentences.
/// The same code appears in the analyzer, the CLI, the scan, the log and the
/// permissions page, so a reader who meets it anywhere can look it up.
/// </summary>
public sealed record PluginRefusal(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("plugin")] string Plugin,
    [property: JsonPropertyName("what")] string What,
    [property: JsonPropertyName("why")] string Why,
    [property: JsonPropertyName("fix")] string Fix,
    [property: JsonPropertyName("severity")] PluginRefusalSeverity Severity
);
