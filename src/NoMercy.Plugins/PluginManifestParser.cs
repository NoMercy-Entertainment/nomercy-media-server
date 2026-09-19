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
using System.Text.RegularExpressions;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Manifest;
using NoMercy.Storage;

namespace NoMercy.Plugins;

public static partial class PluginManifestParser
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

    [GeneratedRegex(
        @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$"
    )]
    private static partial Regex SemverPattern();

    public static PluginManifestV3 ParseV3(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        PluginManifestV3? manifest;

        try
        {
            manifest = JsonSerializer.Deserialize<PluginManifestV3>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw Refuse(
                "The manifest does not read as a contract v3 plugin.json.",
                $"The reader stopped at {exception.Message}",
                "Match plugin.json to the schema. Docs: /nomercy-plugins/reference/manifest"
            );
        }

        if (manifest is null)
        {
            throw Refuse(
                "The manifest is empty.",
                "A plugin.json has to carry the object the schema states.",
                "Write the manifest fields the schema states. Docs: /nomercy-plugins/reference/manifest"
            );
        }

        ValidateV3(manifest);
        return manifest;
    }

    private static void ValidateV3(PluginManifestV3 manifest)
    {
        if (manifest.Id == PluginId.Empty)
        {
            throw Refuse(
                "The manifest carries no id.",
                "Every plugin is identified by the id the marketplace issued it.",
                "Put the id the marketplace issued in plugin.json. Docs: /nomercy-plugins/reference/manifest"
            );
        }

        if (!SemverPattern().IsMatch(manifest.Version))
        {
            throw Refuse(
                $"The version {manifest.Version} is not a version this server reads.",
                "A plugin version is semver, so the marketplace can order releases and a dependency range can name one.",
                "Write the version as semver, for example 2.0.0 or 2.0.0-beta.1. Docs: /nomercy-plugins/reference/manifest"
            );
        }

        if (!PluginAbi.IsCompatible(manifest.TargetAbi))
        {
            throw Refuse(
                $"The manifest targets ABI {manifest.TargetAbi}.",
                $"This server loads ABI {PluginAbi.Current} and the whole previous major.",
                "Build against the current SDK and raise targetAbi. Docs: /nomercy-plugins/reference/manifest"
            );
        }

        foreach (PluginCapabilityGrantRequest capability in manifest.Capabilities)
        {
            if (PluginCapabilityVocabulary.ByName(capability.Name) is null)
            {
                throw Refuse(
                    $"The manifest asks for {capability.Name}, which is not a capability.",
                    "The server grants only the capabilities its vocabulary declares.",
                    $"Take the name from the capability list. Docs: /nomercy-plugins/capabilities/{capability.Name.Replace('.', '-')}"
                );
            }
        }
    }

    private static PluginRefusedException Refuse(string what, string why, string fix)
    {
        return new PluginRefusedException(
            new PluginRefusal(
                PluginRefusalCodes.ManifestInvalid,
                "plugin.json",
                what,
                why,
                fix,
                PluginRefusalSeverity.Blocked
            )
        );
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
        bool trusted = false
    )
    {
        ArgumentNullException.ThrowIfNull(manifest);

        Version version = Version.Parse(manifest.Version);

        return new()
        {
            Id = manifest.Id,
            Name = manifest.Name,
            Description = manifest.Description,
            Version = version,
            Status = status,
            Author = manifest.Author,
            ProjectUrl = manifest.ProjectUrl,
            AssemblyPath = assemblyPath,
            TargetAbi = manifest.TargetAbi,
            ManifestPath = manifestPath,
            Verified = verified,
            Trusted = trusted,
            Capabilities = manifest.Capabilities,
        };
    }

    private static void Validate(PluginManifest manifest)
    {
        if (manifest.Id == Ulid.Empty)
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
