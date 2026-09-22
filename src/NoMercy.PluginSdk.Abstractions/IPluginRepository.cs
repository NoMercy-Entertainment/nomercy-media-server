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

public interface IPluginRepository
{
    /// <summary>
    /// Loads whatever was persisted. Defaulted: an implementation that keeps no
    /// state on disk has nothing to do here, and one written before this member
    /// existed keeps compiling.
    /// </summary>
    Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

    IReadOnlyList<PluginRepositoryInfo> GetRepositories();
    Task AddRepositoryAsync(string name, string url, CancellationToken ct = default);
    Task RemoveRepositoryAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Marks a repository trusted, or takes that back.
    /// <para>
    /// Trust is the strongest thing an owner says about where plugins come
    /// from, and it used to be changeable only by editing repositories.json on
    /// the server by hand. Defaulted to a refusal rather than to doing nothing:
    /// an implementation that cannot record this must say so, not accept the
    /// call and leave the owner believing they changed something.
    /// </para>
    /// </summary>
    Task SetRepositoryTrustAsync(string name, bool trusted, CancellationToken ct = default) =>
        throw new NotSupportedException("This repository cannot change what it trusts.");
    Task RefreshAsync(CancellationToken ct = default);
    IReadOnlyList<PluginRepositoryEntry> GetAvailablePlugins();
    PluginRepositoryEntry? FindPlugin(Ulid pluginId);
    PluginVersionEntry? FindVersion(Ulid pluginId, string version);

    /// <summary>
    /// Whether this plugin is listed by a repository the owner marked trusted.
    /// <para>
    /// False by default, and false when no index could be read: a server that
    /// cannot reach the internet must not decide a plugin is trusted because it
    /// failed to check. The plugin then goes through the ordinary consent, which
    /// is the answer that was always safe.
    /// </para>
    /// </summary>
    bool IsFromTrustedRepository(Ulid pluginId) => false;
}

public class PluginRepositoryInfo
{
    public required string Name { get; init; }
    public required string Url { get; init; }
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether a plugin listed here comes from somewhere the owner trusts.
    /// <para>
    /// Provenance, not consent. It used to enable a plugin on install, which
    /// meant a repository flag answered the consent question on the owner's
    /// behalf for every plugin that index ever lists. From Phase 3 it skips the
    /// marketplace review hold — a delay before a release is published — and
    /// never the owner's decision about their own machine.
    /// </para>
    /// <para>
    /// Trust belongs to where a plugin came from, not to what its manifest says
    /// about itself: an author line is free text any file can copy, and a list
    /// of blessed plugin ids in the source would be a security decision that
    /// needs a rebuild to change. A repository is already a thing the owner adds,
    /// removes and can see.
    /// </para>
    /// <para>
    /// Set on the index we publish because the owner installing our server has
    /// already decided to trust us; every other repository starts untrusted and
    /// the owner turns it on if they mean to.
    /// </para>
    /// </summary>
    public bool Trusted { get; set; }
}
