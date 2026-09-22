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

namespace NoMercy.PluginSdk.Abstractions;

public class PluginInfo
{
    public required Ulid Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required Version Version { get; init; }
    public required PluginStatus Status { get; set; }

    /// <summary>
    /// Why this plugin is malfunctioning, in the words the owner is shown.
    /// <para>
    /// The reason was written to the log and thrown away, so every surface
    /// could say a plugin had malfunctioned and none could say why. An owner
    /// then has a broken plugin, no author to ask, and nothing to send them.
    /// </para>
    /// </summary>
    public string? Malfunction { get; set; }

    public string? Author { get; init; }
    public string? ProjectUrl { get; init; }

    /// <summary>
    /// The license the manifest names, drawn on the host-owned license page.
    ///
    /// An owner deciding whether to keep a plugin should not have to open a
    /// repository to read what they agreed to when they installed it.
    /// </summary>
    public string? License { get; init; }

    /// <summary>Where the plugin's own documentation lives, if it has any.</summary>
    public string? Docs { get; init; }

    public string? AssemblyPath { get; init; }
    public string? TargetAbi { get; init; }
    public string? ManifestPath { get; init; }
    public bool Verified { get; init; }
    public bool Trusted { get; init; }
    public PluginCapabilities? Capabilities { get; init; }

    /// <summary>The fields the host draws on this plugin's settings page.</summary>
    public IReadOnlyList<PluginSettingsField> Settings { get; init; } = [];

    /// <summary>Other plugins this one needs before it can run.</summary>
    public IReadOnlyList<PluginDependency> Dependencies { get; init; } = [];

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
