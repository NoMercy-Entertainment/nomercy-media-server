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

/// <summary>What a plugin is being asked to do.</summary>
public enum PluginOperation
{
    Install,
    Enable,
    Disable,
    Uninstall,
    Update,
}

/// <summary>
/// Why a restart is needed, when one is.
/// <para>
/// Flags rather than a bool, because "restart the server" is a big ask and the
/// owner deserves to know which part of their plugin is asking for it. It is
/// also how we can tell them nothing is needed, which is the far more common
/// case and the one that currently goes unsaid — so every plugin action reads
/// as though it might need a restart, and users restart out of superstition.
/// </para>
/// </summary>
[Flags]
public enum PluginRestartReason
{
    None = 0,

    /// <summary>
    /// The plugin registers its own services. The host's container is built
    /// once at startup and sealed, so services from a plugin installed or
    /// enabled afterwards are not in it and cannot be added.
    /// </summary>
    ContributesServices = 1,

    /// <summary>
    /// The plugin owns REST routes. MVC's application parts are collected
    /// before the pipeline is built, so its endpoints do not exist until the
    /// next start.
    /// </summary>
    OwnsRoutes = 2,

    /// <summary>
    /// Its assembly is still loaded. Unloading a collectible context is
    /// best-effort — one live reference anywhere keeps it — and on Windows the
    /// file stays locked while it is, so the plugin cannot be replaced or
    /// removed from disk until the process exits.
    /// </summary>
    AssemblyStillLoaded = 4,
}

/// <param name="Reasons">Empty when the operation takes effect immediately.</param>
public record PluginRestartRequirement(PluginRestartReason Reasons)
{
    public static PluginRestartRequirement None { get; } = new(PluginRestartReason.None);

    public bool Required => Reasons != PluginRestartReason.None;

    /// <summary>
    /// One sentence per reason, in the owner's terms rather than the runtime's.
    /// Empty when nothing is required, so the dashboard can say so plainly.
    /// </summary>
    public IReadOnlyList<string> Explain()
    {
        List<string> reasons = [];

        if (Reasons.HasFlag(PluginRestartReason.ContributesServices))
            reasons.Add("It adds services the server can only pick up at startup.");

        if (Reasons.HasFlag(PluginRestartReason.OwnsRoutes))
            reasons.Add("It adds API endpoints that are only routed at startup.");

        if (Reasons.HasFlag(PluginRestartReason.AssemblyStillLoaded))
            reasons.Add("Its files are still in use and cannot be changed until the server stops.");

        return reasons;
    }
}
