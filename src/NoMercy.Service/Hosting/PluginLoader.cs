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

using NoMercy.Api.Plugins;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Hooks;
using NoMercy.PluginSdk.Revocation;

namespace NoMercy.Service.Hosting;

public class PluginLoader : IPluginLoader
{
    private readonly ILogger<PluginLoader> _logger;
    private readonly IPluginManager _pluginManager;
    private readonly IPluginCronRegistrar _cronRegistrar;
    private readonly PluginApplicationPartRegistrar _partRegistrar;
    private readonly IPluginRepository _repository;
    private readonly PluginRevocationStartupRefresh _revocations;
    private readonly IPluginRevocationEnforcer _enforcer;

    public PluginLoader(
        ILogger<PluginLoader> logger,
        IPluginManager pluginManager,
        IPluginCronRegistrar cronRegistrar,
        PluginApplicationPartRegistrar partRegistrar,
        IPluginRepository repository,
        PluginRevocationStartupRefresh revocations,
        IPluginRevocationEnforcer enforcer
    )
    {
        _enforcer = enforcer;
        _revocations = revocations;
        _logger = logger;
        _pluginManager = pluginManager;
        _cronRegistrar = cronRegistrar;
        _partRegistrar = partRegistrar;
        _repository = repository;
    }

    public async Task<IReadOnlyList<PluginLoadResult>> LoadPlugins(CancellationToken ct)
    {
        _logger.LogInformation("Loading plugins...");

        // Before the plugins themselves: the catalogue is what an update check
        // reads, and a dashboard that opens to an empty repository list reads as
        // "you never added one".
        await _repository.LoadAsync(ct);

        // Beside the boot, not in it: a server that cannot reach the index must
        // still start, on the catalogue LoadAsync read from the last refresh.
        _ = Task.Run(() => RefreshCatalogueAsync(ct), CancellationToken.None);

        // Asked before the load, so a build revoked while the server was off stays off (AUD-1134).
        await _revocations.RefreshBeforeLoadAsync(ct);

        IReadOnlyList<PluginLoadResult> loadedPlugins = await _pluginManager.LoadAllAsync(ct);

        // A list saved while the load ran is read once more (AUD-1134).
        await _enforcer.StopRevokedAsync(ct);

        foreach (PluginLoadResult pluginResult in loadedPlugins)
        {
            _logger.LogInformation(
                "Plugin loaded: {Name} {Version} ({PluginId})",
                [pluginResult.Name, pluginResult.Version, pluginResult.PluginId]
            );
        }

        _cronRegistrar.RegisterAll();

        // Plugins load after the container is built, which is long after MVC
        // fixed its route table. Without this a plugin's controllers exist and
        // are unreachable, which reads as an installed plugin doing nothing.
        _partRegistrar.AttachAll(_pluginManager);

        return loadedPlugins;
    }

    private async Task RefreshCatalogueAsync(CancellationToken ct)
    {
        try
        {
            await _repository.RefreshAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The server is shutting down; the next start refreshes again.
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "The plugin catalogue could not be refreshed at start; the cached one stays in use."
            );
        }
    }
}
