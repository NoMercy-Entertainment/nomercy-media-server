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

public static class PluginDependencyResolver
{
    public static PluginRefusal? Resolve(
        PluginManifest manifest,
        IReadOnlyList<PluginManifest> installed
    )
    {
        string plugin = $"{manifest.Name} {manifest.Version}";

        foreach (PluginDependency dependency in manifest.Dependencies)
        {
            if (manifest.Tier == PluginTier.Free && dependency.Tier == PluginTier.Paid)
            {
                return new PluginRefusal(
                    PluginRefusalCodes.DependencyTierMismatch,
                    plugin,
                    $"The plugin depends on the paid plugin {dependency.Id}.",
                    "A free plugin may depend only on free plugins, so nobody has to buy something to run something that is free.",
                    "Remove the dependency from plugin.json, or list this plugin as paid, because a free plugin may depend only on free plugins. Docs: /nomercy-plugins/tour/dependencies",
                    PluginRefusalSeverity.Blocked
                );
            }

            PluginManifest? match = installed.FirstOrDefault(candidate =>
                candidate.Id == dependency.Id
            );

            if (match is null && dependency.Tier == PluginTier.Paid)
            {
                return new PluginRefusal(
                    PluginRefusalCodes.DependencyPaidNotOwned,
                    plugin,
                    $"The plugin depends on the paid plugin {dependency.Id}, which this server does not own.",
                    "A paid dependency is never bought for you, so the plugin installs and stays off until you own it.",
                    $"Buy {dependency.Id} on nomercy.tv, then enable this plugin. Docs: /nomercy-plugins/tour/dependencies",
                    PluginRefusalSeverity.Blocked
                );
            }

            if (match is null || !SemverRange.Satisfies(match.Version, dependency.Range))
            {
                return new PluginRefusal(
                    PluginRefusalCodes.DependencyMissing,
                    plugin,
                    $"The plugin needs {dependency.Id} {dependency.Range}, which is not installed.",
                    "A dependency that is missing, revoked, abandoned or removed pauses everything that depends on it.",
                    $"Install {dependency.Id} {dependency.Range} from the marketplace. Docs: /nomercy-plugins/tour/dependencies",
                    PluginRefusalSeverity.Blocked
                );
            }
        }

        return null;
    }
}
