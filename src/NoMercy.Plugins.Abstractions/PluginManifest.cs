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

/// <summary>
/// The one shape a plugin.json is read against. Every field an older
/// plugin.json does not carry is optional with a sensible default, so a
/// manifest written before a field existed keeps loading rather than needing
/// a second, tolerant type beside this one.
/// </summary>
public sealed record PluginManifest
{
    [JsonPropertyName("id")]
    public required PluginId Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("targetAbi")]
    public string? TargetAbi { get; init; }

    [JsonPropertyName("publisher")]
    public PluginId? Publisher { get; init; }

    [JsonPropertyName("tier")]
    public PluginTier Tier { get; init; } = PluginTier.Free;

    [JsonPropertyName("assembly")]
    public required string Assembly { get; init; }

    [JsonPropertyName("entry")]
    public string Entry { get; init; } = string.Empty;

    [JsonPropertyName("author")]
    public string? Author { get; init; }

    [JsonPropertyName("license")]
    public string? License { get; init; }

    [JsonPropertyName("projectUrl")]
    public string? ProjectUrl { get; init; }

    [JsonPropertyName("docs")]
    public string? Docs { get; init; }

    [JsonPropertyName("autoEnabled")]
    public bool AutoEnabled { get; init; } = true;

    [JsonPropertyName("translations")]
    public PluginTranslations? Translations { get; init; }

    [JsonPropertyName("capabilities")]
    public PluginCapabilities? Capabilities { get; init; }

    [JsonPropertyName("dependencies")]
    public IReadOnlyList<PluginDependency> Dependencies { get; init; } = [];

    [JsonPropertyName("ui")]
    public PluginUiSpec? Ui { get; init; }

    /// <summary>
    /// The fields the host draws on this plugin's settings page. Declared here
    /// rather than rendered by the plugin, so the same settings reach a remote
    /// control and a phone without the author drawing three pages.
    /// </summary>
    [JsonPropertyName("settings")]
    public IReadOnlyList<PluginSettingsField> Settings { get; init; } = [];

    [JsonPropertyName("signature")]
    public PluginSignatureBlock? Signature { get; init; }
}
