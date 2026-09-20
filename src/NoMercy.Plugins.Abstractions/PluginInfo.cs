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

namespace NoMercy.Plugins.Abstractions;

public class PluginInfo
{
    public required Ulid Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required Version Version { get; init; }
    public required PluginStatus Status { get; set; }
    public string? Author { get; init; }
    public string? ProjectUrl { get; init; }
    public string? AssemblyPath { get; init; }
    public string? TargetAbi { get; init; }
    public string? ManifestPath { get; init; }
    public bool Verified { get; init; }
    public bool Trusted { get; init; }
    public PluginCapabilities? Capabilities { get; init; }

    /// <summary>What the marketplace says this costs. Free until it says otherwise.</summary>
    public PluginTier Tier { get; init; } = PluginTier.Free;

    /// <summary>
    /// Installed from a file the owner supplied rather than from a repository.
    /// Owner-only and never verified, because nothing can say who wrote it.
    /// </summary>
    public bool Sideloaded { get; init; }

    /// <summary>
    /// Whether the assembly carries an <see cref="IPluginServiceRegistrator"/>.
    /// <para>Decided at load rather than guessed from the manifest, because it
    /// is what determines whether enabling this plugin can take full effect
    /// without a restart — the host's container is sealed once built.</para>
    /// </summary>
    public bool ContributesServices { get; set; }
}
