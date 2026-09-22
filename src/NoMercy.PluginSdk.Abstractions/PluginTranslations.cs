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
/// The translations a plugin ships with it.
///
/// A plugin builds a view out of components whose text it supplies, so the
/// server sends strings and the client cannot translate what it has never seen.
/// The plugin bundles them instead, the same way the player libraries do: files
/// beside the assembly, checked when the plugin loads rather than when a viewer
/// first opens the page in a language nobody tested.
/// </summary>
public class PluginTranslations
{
    /// <summary>
    /// The locale the plugin is authored in. Every key exists here, and every
    /// other locale is measured against it.
    /// </summary>
    [JsonPropertyName("source")]
    public string Source { get; init; } = "en";

    /// <summary>
    /// The locales shipped, as tags such as `en` or `nl`. The plugin provides
    /// `lang/&lt;locale&gt;.json` for each.
    /// </summary>
    [JsonPropertyName("locales")]
    public List<string> Locales { get; init; } = [];

    /// <summary>
    /// The directory holding the files, relative to the plugin's own folder.
    /// </summary>
    [JsonPropertyName("path")]
    public string Path { get; init; } = "lang";
}

/// <summary>
/// What a validation pass found.
/// </summary>
public class PluginTranslationProblem
{
    public required string Locale { get; init; }

    public required string Detail { get; init; }

    public override string ToString()
    {
        return $"{Locale}: {Detail}";
    }
}
