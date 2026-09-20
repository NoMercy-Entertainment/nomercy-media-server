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

using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugins.Manifest;

/// <summary>
/// A v2 manifest, read as v3. The table is design section 9; nothing the owner
/// already approved is asked again because the capability names map one to one.
/// </summary>
public static class PluginManifestV2Mapper
{
    private static readonly Dictionary<string, string[]> HookMap = new()
    {
        ["ui"] = [PluginCapabilityNames.UiMount],
        ["scheduledTask"] = [PluginCapabilityNames.Scheduler],
        ["mediaSource"] = [PluginCapabilityNames.MediaSource],
        ["metadata"] = [PluginCapabilityNames.MetadataProvide],
        ["libraryWrite"] = [PluginCapabilityNames.LibraryWrite],
        ["encoder"] = [PluginCapabilityNames.EncoderProfile, PluginCapabilityNames.EncoderDispatch],
        ["storage"] = [PluginCapabilityNames.StoragePath],
        ["audioTools"] = [PluginCapabilityNames.AudioTools],
        ["derivedAudio"] = [PluginCapabilityNames.StorageDerived],
        ["musicAnalysisWrite"] = [PluginCapabilityNames.MusicAnalysisWrite],
        ["auth"] = [PluginCapabilityNames.AuthClaims],
    };

    public static PluginManifestV3 ToV3(PluginManifest manifest)
    {
        return ToV3(manifest, out PluginRefusal _);
    }

    public static PluginManifestV3 ToV3(PluginManifest manifest, out PluginRefusal notice)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        List<PluginCapabilityGrantRequest> capabilities = [];

        foreach (string hook in manifest.Capabilities?.Hooks ?? [])
        {
            if (!HookMap.TryGetValue(hook, out string[]? names))
            {
                continue;
            }

            capabilities.AddRange(
                names.Select(name => new PluginCapabilityGrantRequest(name, null, null))
            );
        }

        foreach (string host in manifest.Capabilities?.Network?.Hosts ?? [])
        {
            capabilities.Add(
                new PluginCapabilityGrantRequest(PluginCapabilityNames.NetworkFetch, host, null)
            );
        }

        if (manifest.Capabilities?.Rest == true)
        {
            capabilities.Add(
                new PluginCapabilityGrantRequest(PluginCapabilityNames.Rest, null, null)
            );
        }

        if (manifest.Capabilities?.Ws == true)
        {
            capabilities.Add(
                new PluginCapabilityGrantRequest(PluginCapabilityNames.Hub, null, null)
            );
        }

        notice = new PluginRefusal(
            PluginRefusalCodes.ManifestV2Deprecated,
            $"{manifest.Name} {manifest.Version}",
            "The plugin ships a contract v2 manifest.",
            "Contract v3 states capabilities as a list of names with a scope and a reason, so the owner can see what each one is for.",
            "Run nomercy-plugin verify and take the v3 manifest it writes. Docs: /nomercy-plugins/reference/migration-from-v2",
            PluginRefusalSeverity.Warning
        );

        return new PluginManifestV3
        {
            Id = new PluginId(manifest.Id),
            Name = manifest.Name,
            Description = manifest.Description,
            Version = manifest.Version,
            TargetAbi = manifest.TargetAbi ?? "10.0",
            Assembly = manifest.Assembly,
            Entry = string.Empty,
            ProjectUrl = manifest.ProjectUrl,
            AutoEnabled = manifest.AutoEnabled,
            Translations = manifest.Translations,
            Capabilities = capabilities,
        };
    }
}
