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
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.Cli;

/// <summary>
/// The manifest half of the marketplace's own gate, run before anyone uploads.
/// <para>
/// Same checks, same wording. A scan that is kinder than the marketplace is a
/// scan that passes a plugin which is then rejected on upload, and the author
/// has no way to tell which of the two was right.
/// </para>
/// </summary>
public static class ManifestScan
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static IReadOnlyList<PluginRefusal> Run(string folder)
    {
        string path = Path.Combine(folder, "plugin.json");

        if (!File.Exists(path))
            return
            [
                new PluginRefusal(
                    PluginRefusalCodes.ManifestInvalid,
                    folder,
                    "There is no plugin.json in this folder.",
                    "A plugin is what its manifest says it is. Without one there is nothing to check and nothing a server could install.",
                    "Run nomercy-plugin new to scaffold one, or run verify from the folder that holds plugin.json. Docs: /nomercy-plugins/tour/manifest",
                    PluginRefusalSeverity.Blocked
                ),
            ];

        PluginManifest? manifest;

        try
        {
            manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path), Options);
        }
        catch (JsonException exception)
        {
            return
            [
                new PluginRefusal(
                    PluginRefusalCodes.ManifestInvalid,
                    folder,
                    $"plugin.json could not be read: {exception.Message}",
                    "A manifest that does not parse cannot be checked at all, so every other finding below it would be a guess.",
                    "Fix the JSON and run verify again. Docs: /nomercy-plugins/tour/manifest",
                    PluginRefusalSeverity.Blocked
                ),
            ];
        }

        if (manifest is null)
            return
            [
                new PluginRefusal(
                    PluginRefusalCodes.ManifestInvalid,
                    folder,
                    "plugin.json is empty.",
                    "There is nothing to check.",
                    "Run nomercy-plugin new to scaffold one. Docs: /nomercy-plugins/tour/manifest",
                    PluginRefusalSeverity.Blocked
                ),
            ];

        List<PluginRefusal> findings = [];
        string who = $"{manifest.Name} {manifest.Version}";

        if (!Version.TryParse(manifest.TargetAbi, out Version? target))
            findings.Add(
                new PluginRefusal(
                    PluginRefusalCodes.ManifestInvalid,
                    who,
                    $"targetAbi is '{manifest.TargetAbi}', which is not a version.",
                    "The server decides whether it can run a plugin by comparing this to its own contract, and a value it cannot read is one it has to refuse.",
                    $"Set targetAbi to {PluginAbi.Current}. Docs: /nomercy-plugins/handbook/versioning",
                    PluginRefusalSeverity.Blocked
                )
            );
        else if (!PluginAbi.IsCompatible(manifest.TargetAbi))
            findings.Add(
                new PluginRefusal(
                    PluginRefusalCodes.AbiUnsupported,
                    who,
                    $"targetAbi is {manifest.TargetAbi} and this server speaks {PluginAbi.Current}.",
                    "The contract has moved on by more than one major since then, so members this plugin was built against are gone rather than merely older.",
                    $"Rebuild against {PluginAbi.Current}. Docs: /nomercy-plugins/handbook/versioning",
                    PluginRefusalSeverity.Blocked
                )
            );
        else if (target.Major < PluginAbi.Current.Major)
            findings.Add(
                new PluginRefusal(
                    PluginRefusalCodes.ManifestInvalid,
                    who,
                    $"targetAbi is {manifest.TargetAbi} and the current contract is {PluginAbi.Current}.",
                    "It still loads. It is worth saying anyway, because the members added since are ones this plugin cannot see and will not be told about.",
                    $"Rebuild against {PluginAbi.Current} when convenient. Docs: /nomercy-plugins/handbook/versioning",
                    PluginRefusalSeverity.Degraded
                )
            );

        foreach (string hook in manifest.Capabilities?.Hooks ?? [])
            if (PluginCapabilityVocabulary.ByName(hook) is null)
                findings.Add(
                    new PluginRefusal(
                        PluginRefusalCodes.ManifestInvalid,
                        who,
                        $"The manifest declares '{hook}', which is not a capability this server knows.",
                        "A name the vocabulary does not carry is one the owner is never asked about, so the plugin is installed without it and refuses the first time it is used.",
                        "Use a name from the capability list. Docs: /nomercy-plugins/capabilities",
                        PluginRefusalSeverity.Blocked
                    )
                );

        foreach (PluginUiMount mount in manifest.Capabilities?.Ui?.Mounts ?? [])
        {
            if (!PluginKind.IsKnown(mount.Section))
                findings.Add(
                    new PluginRefusal(
                        PluginRefusalCodes.ManifestInvalid,
                        who,
                        $"A mount names section '{mount.Section}', which is not a kind.",
                        "The kind decides where a plugin's screens live on every client, so one nobody recognises is a plugin that is drawn nowhere.",
                        $"Use one of: {string.Join(", ", PluginKind.All)}. Docs: /nomercy-plugins/tour/placement",
                        PluginRefusalSeverity.Blocked
                    )
                );

            if (mount.Label.Contains(' '))
                findings.Add(
                    new PluginRefusal(
                        PluginRefusalCodes.ManifestInvalid,
                        who,
                        $"The mount label '{mount.Label}' reads as a sentence rather than a translation key.",
                        "A label written here is written once, in one language, and every owner whose language is not that one reads it anyway.",
                        "Put the text in lang/en.json and name its key here. Docs: /nomercy-plugins/tour/translations",
                        PluginRefusalSeverity.Degraded
                    )
                );
        }

        return findings;
    }
}
