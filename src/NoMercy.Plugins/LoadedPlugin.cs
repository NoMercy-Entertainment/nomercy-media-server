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

namespace NoMercy.Plugins;

/// <summary>
/// A plugin that has been discovered and loaded: its metadata, the live instance
/// (null when the plugin is disabled or malfunctioned), and the assembly load
/// context that owns its assemblies.
/// </summary>
internal sealed class LoadedPlugin(
    PluginInfo info,
    IPlugin? instance,
    PluginLoadContext? loadContext,
    string? shadowDirectory = null,
    PluginServiceProvider? serviceProvider = null
)
{
    public PluginInfo Info { get; } = info;
    public IPlugin? Instance { get; set; } = instance;
    public PluginLoadContext? LoadContext { get; } = loadContext;

    /// <summary>
    /// The plugin's own container, or null when it registered no services of
    /// its own. Disposed when the plugin unloads, before the load context, so
    /// nothing is finalized on a type whose assembly has gone.
    /// </summary>
    public PluginServiceProvider? ServiceProvider { get; } = serviceProvider;

    /// <summary>
    /// The per-load copy the assemblies were loaded from (see
    /// <see cref="PluginShadowCopy"/>), or null when nothing was loaded.
    /// </summary>
    public string? ShadowDirectory { get; } = shadowDirectory;

    /// <summary>The assembly file the runtime actually mapped, when one was.</summary>
    public string? LoadedAssemblyPath =>
        ShadowDirectory is null || Info.AssemblyPath is null
            ? null
            : Path.Combine(ShadowDirectory, Path.GetFileName(Info.AssemblyPath));
}
