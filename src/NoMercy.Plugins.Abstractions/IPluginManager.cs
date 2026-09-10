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

public interface IPluginManager
{
    IReadOnlyList<PluginInfo> GetInstalledPlugins();
    Task InstallPluginAsync(string packageUrl, CancellationToken ct = default);

    // Repository-backed install: verifies the supplied checksum before anything
    // is copied to disk. Default forwards to the checksum-less overload so
    // existing implementers (test doubles) keep compiling unchanged.
    Task InstallPluginAsync(
        string packageUrl,
        string? expectedChecksum,
        CancellationToken ct = default
    ) => InstallPluginAsync(packageUrl, ct);

    /// <summary>
    /// Installs a plugin from a packaged archive: a zip carrying the plugin's
    /// folder, manifest and every file it ships with.
    /// <para>
    /// The bare-assembly overloads above copy one file, which throws away the
    /// plugin.json beside it and every dependency the plugin brought. That is
    /// fine for a plugin that is one self-contained dll and wrong for every
    /// other one, which is why real plugins are published as archives.
    /// </para>
    /// <para>
    /// Defaulted to a refusal rather than silently doing nothing: an
    /// implementation that cannot unpack an archive must say so, not accept the
    /// call and leave the caller thinking a plugin was installed.
    /// </para>
    /// </summary>
    Task InstallPluginArchiveAsync(
        string archivePath,
        string? expectedChecksum = null,
        CancellationToken ct = default
    ) => throw new NotSupportedException("This plugin manager cannot install from an archive.");

    Task EnablePluginAsync(Ulid pluginId, CancellationToken ct = default);
    Task DisablePluginAsync(Ulid pluginId, CancellationToken ct = default);

    // Disable then enable in one call, so a caller does not have to fake a
    // restart out of two separate requests. Defaulted so existing implementers
    // (test doubles) keep compiling.
    Task RestartPluginAsync(Ulid pluginId, CancellationToken ct = default) =>
        throw new NotSupportedException("This plugin manager cannot restart a plugin.");

    Task UninstallPluginAsync(Ulid pluginId, CancellationToken ct = default);

    // Boot-time scan: load all plugins in the plugins directory, isolating failures
    // per plugin so one bad plugin never blocks the others.
    Task<IReadOnlyList<PluginLoadResult>> LoadAllAsync(CancellationToken ct = default);

    // Loaded plugin instances implementing T (e.g. IEncoderPlugin). Empty when none.
    IEnumerable<T> GetPluginsOfType<T>()
        where T : IPlugin;

    // A single plugin's registration. Hubs, action filters and the UI endpoints
    // all need one plugin by id on a request path; scanning the whole installed
    // list for it is the shape those call sites had to use before. The default
    // does exactly that scan so existing implementers keep compiling.
    PluginInfo? GetPluginInfo(Ulid pluginId) =>
        GetInstalledPlugins().FirstOrDefault(info => info.Id == pluginId);

    // The plugin's strings for one locale, read from the files it ships beside
    // its assembly, falling back to the locale the plugin was authored in when
    // it ships nothing for the one asked for. A default returning null keeps
    // existing implementers compiling; a manager that knows where a plugin lives
    // on disk overrides it.
    Task<Dictionary<string, string>?> ReadTranslationsAsync(
        Ulid pluginId,
        string locale,
        CancellationToken ct
    ) => Task.FromResult<Dictionary<string, string>?>(null);

    // The live instance, for the platform endpoints that have to call into a
    // plugin (IUiPlugin.GetViewAsync). Null when nothing is loaded under that id.
    IPlugin? GetPluginInstance(Ulid pluginId) => null;
}
