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
using NoMercy.Plugins.Abstractions;
using NoMercy.Storage;

namespace NoMercy.Plugins;

public static class PluginManifestParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static PluginManifest Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        PluginManifest? manifest = JsonSerializer.Deserialize<PluginManifest>(json, JsonOptions);

        if (manifest is null)
        {
            throw new InvalidOperationException("Failed to deserialize plugin manifest.");
        }

        Validate(manifest);
        return manifest;
    }

    public static async Task<PluginManifest> ParseFileAsync(
        string filePath,
        IStorage storage,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(storage);

        if (!storage.Exists(filePath))
        {
            throw new FileNotFoundException($"Plugin manifest not found: {filePath}", filePath);
        }

        string json = await storage.ReadAllTextAsync(filePath, ct);
        return Parse(json);
    }

    public static PluginInfo ToPluginInfo(
        PluginManifest manifest,
        string assemblyPath,
        PluginStatus status,
        string? manifestPath = null,
        bool verified = false,
        bool trusted = false,
        bool sideloaded = false
    )
    {
        ArgumentNullException.ThrowIfNull(manifest);

        Version version = Version.Parse(manifest.Version);

        return new()
        {
            Id = manifest.Id.Value,
            Name = manifest.Name,
            Description = manifest.Description,
            Version = version,
            Status = status,
            Author = manifest.Author,
            ProjectUrl = manifest.ProjectUrl,
            License = manifest.License,
            Docs = manifest.Docs,
            AssemblyPath = assemblyPath,
            TargetAbi = manifest.TargetAbi,
            ManifestPath = manifestPath,
            Verified = verified,
            Trusted = trusted,
            Capabilities = manifest.Capabilities,
            Settings = manifest.Settings,
            Dependencies = manifest.Dependencies,
            Tier = manifest.Tier,
            Sideloaded = sideloaded,
        };
    }

    private static void Validate(PluginManifest manifest)
    {
        if (manifest.Id == PluginId.Empty)
        {
            throw new InvalidOperationException("Plugin manifest 'id' must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            throw new InvalidOperationException("Plugin manifest 'name' is required.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            throw new InvalidOperationException("Plugin manifest 'version' is required.");
        }

        if (!Version.TryParse(manifest.Version, out _))
        {
            throw new InvalidOperationException(
                $"Plugin manifest 'version' is not a valid version string: '{manifest.Version}'."
            );
        }

        if (string.IsNullOrWhiteSpace(manifest.Description))
        {
            throw new InvalidOperationException("Plugin manifest 'description' is required.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Assembly))
        {
            throw new InvalidOperationException("Plugin manifest 'assembly' is required.");
        }
    }
}
