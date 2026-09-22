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

namespace NoMercy.PluginSdk.Capabilities;

/// <summary>
/// What one plugin declared, and nothing else.
/// <para>
/// The broker only needs this. Taking the whole plugin manager would let the
/// thing that decides whether a plugin may act also install, enable and
/// uninstall it, and would make every test of a permission decision carry a
/// double for eleven methods it never calls.
/// </para>
/// </summary>
public interface IPluginManifestSource
{
    PluginInfo? Find(Ulid pluginId);

    /// <summary>Every plugin installed here, for the answers that are about all of them.</summary>
    IReadOnlyList<PluginInfo> All();
}

/// <summary>
/// The manager, narrowed. Takes it lazily: this source is built while
/// <see cref="NoMercy.PluginSdk.PluginManager"/> itself is still under
/// construction (its constructor resolves the out-of-process runtime, which
/// resolves the capability broker, which resolves this source) — an eager
/// <see cref="IPluginManager"/> parameter here re-enters the manager's own
/// factory forever, the same cycle the hub router and cron registrar in
/// PluginServiceCollectionExtensions.AddPluginSystem already avoid this way.
/// </summary>
public sealed class PluginManagerManifestSource(Func<IPluginManager> plugins)
    : IPluginManifestSource
{
    public PluginInfo? Find(Ulid pluginId) => plugins().GetPluginInfo(pluginId);

    public IReadOnlyList<PluginInfo> All() => [.. plugins().GetInstalledPlugins()];
}
