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

public sealed record PluginManifestV3
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
    public required string TargetAbi { get; init; }

    [JsonPropertyName("publisher")]
    public PluginId? Publisher { get; init; }

    [JsonPropertyName("tier")]
    public PluginTier Tier { get; init; } = PluginTier.Free;

    [JsonPropertyName("assembly")]
    public required string Assembly { get; init; }

    [JsonPropertyName("entry")]
    public required string Entry { get; init; }

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
    public IReadOnlyList<PluginCapabilityGrantRequest> Capabilities { get; init; } = [];

    [JsonPropertyName("dependencies")]
    public IReadOnlyList<PluginDependency> Dependencies { get; init; } = [];

    [JsonPropertyName("ui")]
    public PluginUiSpec? Ui { get; init; }

    [JsonPropertyName("signature")]
    public PluginSignatureBlock? Signature { get; init; }
}
