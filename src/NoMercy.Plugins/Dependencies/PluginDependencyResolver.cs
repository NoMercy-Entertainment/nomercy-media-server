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
using NoMercy.Plugins.Capabilities;

namespace NoMercy.Plugins.Dependencies;

/// <summary>What the repositories offer, narrowed to what a plan needs to know.</summary>
public interface IPluginCatalogue
{
    PluginCatalogueEntry? Find(Ulid pluginId);
}

/// <summary>One plugin a repository offers.</summary>
public sealed record PluginCatalogueEntry(Ulid PluginId, PluginTier Tier, Version Version);

/// <summary>The repositories the owner added, narrowed to the one question a plan asks.</summary>
public sealed class PluginRepositoryCatalogue(IPluginRepository repository) : IPluginCatalogue
{
    public PluginCatalogueEntry? Find(Ulid pluginId)
    {
        if (repository.FindPlugin(pluginId) is not { } entry)
            return null;

        Version newest = entry
            .Versions.Select(version =>
                Version.TryParse(version.Version, out Version? parsed) ? parsed : null
            )
            .Where(parsed => parsed is not null)
            .DefaultIfEmpty(null)
            .Max()!;

        return newest is null ? null : new(pluginId, entry.Tier, newest);
    }
}

/// <summary>
/// What installs alongside a plugin, in the same step.
/// <para>
/// Free dependencies only. A paid one is a purchase, and a purchase made by
/// pressing install on something else is a purchase nobody made.
/// </para>
/// </summary>
public class PluginDependencyResolver(IPluginCatalogue catalogue, IPluginManifestSource installed)
{
    public IReadOnlyList<Ulid> Plan(IReadOnlyList<PluginDependency> dependencies) =>
        [
            .. dependencies
                .Select(dependency => dependency.Id.Value)
                .Where(id => installed.Find(id) is null)
                .Where(id => catalogue.Find(id) is { Tier: PluginTier.Free })
                .Distinct(),
        ];
}
