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

using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginSdk.Provenance;

/// <summary>
/// Joins the recorded install with the repository list as it is now.
/// <para>
/// Trust is looked up on every read, never copied at install: an owner who
/// stops trusting a repository means every plugin it supplied, including the
/// ones already installed. The repository is matched by its index URL, the
/// same key the cached catalogue uses, so a repository renamed or re-added
/// under another name is still the index the plugin came from.
/// </para>
/// </summary>
public class PluginProvenanceReader(
    IPluginInstallSourceStore sources,
    IPluginRepository repositories
) : IPluginProvenanceReader
{
    public PluginProvenance Read(Ulid pluginId)
    {
        PluginInstallSource? source = sources.Find(pluginId);

        if (source is null)
            return PluginProvenance.Unknown;

        bool hasRepository = !string.IsNullOrWhiteSpace(source.RepositoryUrl);
        PluginRepositoryInfo? repository = hasRepository
            ? repositories
                .GetRepositories()
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Url, source.RepositoryUrl, StringComparison.Ordinal)
                )
            : null;

        return new()
        {
            Kind = source.Kind,
            RepositoryName = source.RepositoryName,
            RepositoryUrl = source.RepositoryUrl,
            // The rule the trusted set itself is built with: enabled and trusted.
            RepositoryTrusted = hasRepository
                ? repository is { Enabled: true, Trusted: true }
                : null,
            RepositoryRemoved = hasRepository ? repository is null : null,
            FromOfficialMarketplace = source.FromOfficialMarketplace,
            ChecksumVerified = source.ChecksumVerified,
            Version = source.Version,
            RecordedAt = source.RecordedAt,
        };
    }
}
