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
using Microsoft.Extensions.Logging;
using NoMercy.Events;
using NoMercy.Events.Plugins;
using NoMercy.Plugins.Abstractions;
using NoMercy.Storage;

namespace NoMercy.Plugins;

/// <summary>
/// Handles plugin lifecycle state transitions (enable, disable, uninstall),
/// coordinating the registry, the loader, and lifecycle events. Splitting this
/// out of <see cref="PluginManager"/> keeps each lifecycle operation in one
/// focused place.
/// </summary>
internal sealed class PluginLifecycleManager(
    IEventBus eventBus,
    IServiceProvider serviceProvider,
    ILogger logger,
    string pluginsPath,
    IStorage storage,
    IPluginRegistry registry,
    PluginLoader loader,
    IPluginContextFactory contextFactory,
    IPluginAssemblyTracker? assemblyTracker = null,
    Action<Ulid>? releaseScheduledWork = null
)
{
    private readonly IEventBus _eventBus = eventBus;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly ILogger _logger = logger;
    private readonly string _pluginsPath = pluginsPath;
    private readonly IStorage _storage = storage;
    private readonly IPluginRegistry _registry = registry;
    private readonly PluginLoader _loader = loader;
    private readonly IPluginContextFactory _contextFactory = contextFactory;
    private readonly IPluginAssemblyTracker? _assemblyTracker = assemblyTracker;
    private readonly Action<Ulid>? _releaseScheduledWork = releaseScheduledWork;

    /// <summary>Looks up an installed plugin, or reports it as not installed.</summary>
    private LoadedPlugin RequireLoaded(Ulid pluginId)
    {
        if (!_registry.TryGetValue(pluginId, out LoadedPlugin? loaded))
        {
            throw new InvalidOperationException($"Plugin {pluginId} is not installed.");
        }

        return loaded;
    }

    public async Task EnablePluginAsync(Ulid pluginId, CancellationToken ct = default)
    {
        LoadedPlugin loaded = RequireLoaded(pluginId);

        if (loaded.Info.Status == PluginStatus.Active)
        {
            return;
        }

        if (loaded.Instance is null && loaded.Info.AssemblyPath is not null)
        {
            await _loader.LoadPluginAssemblyAsync(loaded.Info.AssemblyPath, ct);
            return;
        }

        if (loaded.Instance is not null)
        {
            try
            {
                string dataFolder = Path.Combine(_pluginsPath, "data", pluginId.ToString());
                if (!_storage.Exists(dataFolder))
                {
                    _storage.CreateDirectory(dataFolder);
                }

                IPluginContext context = _contextFactory.Create(
                    pluginId,
                    dataFolder,
                    _logger,
                    loaded.Info.Capabilities,
                    loaded.Instance.Name,
                    loaded.Instance.Version
                );
                loaded.Instance.Initialize(context);
                PluginLifecycle.Transition(loaded.Info, PluginStatus.Active);

                await _eventBus.PublishAsync(
                    new PluginLoadedEvent
                    {
                        PluginId = pluginId.ToString(),
                        PluginName = loaded.Info.Name,
                        Version = loaded.Info.Version.ToString(),
                    },
                    ct
                );
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Not PluginLifecycle.Transition: this recovery path runs from
                // whatever pre-Active status let us reach the Instance-not-null
                // branch above (Disabled or Malfunctioned — Active already
                // short-circuited at the top of this method), and neither of
                // those has Malfunctioned in its allowed-transitions set, so
                // routing through Transition here throws and replaces this
                // graceful failure record with an unhandled exception. Setting
                // Status directly matches how PluginLoader already records a
                // malfunction on a freshly built PluginInfo.
                loaded.Info.Status = PluginStatus.Malfunctioned;

                await _eventBus.PublishAsync(
                    new PluginErrorOccurredEvent
                    {
                        PluginId = pluginId.ToString(),
                        PluginName = loaded.Info.Name,
                        ErrorMessage = ex.Message,
                        ExceptionType = ex.GetType().Name,
                    },
                    ct
                );
            }
        }
    }

    public async Task DisablePluginAsync(Ulid pluginId, CancellationToken ct = default)
    {
        LoadedPlugin loaded = RequireLoaded(pluginId);

        if (loaded.Info.Status == PluginStatus.Disabled)
        {
            return;
        }

        // Before Dispose, because a cron executor holds the instance: leaving
        // one registered keeps the plugin's load context alive for the rest of
        // the process, and with it the lock on its files.
        _releaseScheduledWork?.Invoke(pluginId);

        loaded.Instance?.Dispose();

        // Dropped, not kept: a disposed instance is dead by IDisposable's own
        // contract, and enabling again took the "Instance is not null" branch and
        // called Initialize on the corpse. The plugin then reported Active while
        // every view request it served hit its own disposed guard — the owner saw
        // "this plugin is disabled or is being unloaded" on a plugin the dashboard
        // listed as running, on every surface, until the server was restarted.
        // Nulling it sends the next enable through the loader, which builds a
        // fresh instance in a fresh load context. This is also what the field has
        // always been documented to mean.
        loaded.Instance = null;

        PluginLifecycle.Transition(loaded.Info, PluginStatus.Disabled);

        // Enabling announced itself and this did not, so a plugin's routes and
        // its hub handler stayed registered after the owner turned it off.
        await _eventBus.PublishAsync(
            new PluginDisabledEvent
            {
                PluginId = pluginId.ToString(),
                PluginName = loaded.Info.Name,
            },
            ct
        );
    }

    /// <summary>
    /// Takes a plugin out of the process so its files can be replaced, and
    /// leaves everything on disk exactly where it is.
    /// <para>
    /// Uninstall does this too, but it also deletes the directory and announces
    /// that the plugin is gone. An update is neither: the files are about to be
    /// replaced by a newer copy of the same plugin, and it comes back a moment
    /// later. Returns whether anything was actually resident, so a caller can
    /// tell "unloaded it" from "there was nothing to unload".
    /// </para>
    /// <para>
    /// No event is published. The plugin is momentarily absent, but every
    /// subscriber that would hear a disable would hear a load again within the
    /// same call, and a pair of those describes a disruption that did not
    /// happen. The load at the end of the update is what gets announced.
    /// </para>
    /// <para>
    /// Note that <c>Unload()</c> only asks. The context goes when the GC
    /// collects it, so the files stay held for a moment after this returns and
    /// the caller has to wait for them rather than assume.
    /// </para>
    /// </summary>
    public Task<bool> UnloadForUpdateAsync(Ulid pluginId, CancellationToken ct = default)
    {
        if (!_registry.TryRemove(pluginId, out LoadedPlugin? loaded))
        {
            return Task.FromResult(false);
        }

        _releaseScheduledWork?.Invoke(pluginId);

        loaded.Instance?.Dispose();

        if (loaded.LoadContext is not null)
        {
            _assemblyTracker?.TrackUnload(pluginId, loaded.Info.AssemblyPath);
            loaded.LoadContext.Unload();
        }

        return Task.FromResult(true);
    }

    /// <summary>
    /// Disable then enable in one call, so the dashboard has a single Restart
    /// action instead of asking the owner to press two buttons for one idea.
    /// Neither half needs the server restarted, so this never does either.
    /// </summary>
    public async Task RestartPluginAsync(Ulid pluginId, CancellationToken ct = default)
    {
        // Not RequireLoaded: a plugin that is not installed at all should be
        // reported by EnablePluginAsync below, once, rather than by a duplicate
        // check here that says the same thing first.
        if (
            _registry.TryGetValue(pluginId, out LoadedPlugin? loaded)
            && loaded.Info.Status == PluginStatus.Active
        )
        {
            await DisablePluginAsync(pluginId, ct);
        }

        await EnablePluginAsync(pluginId, ct);
    }

    public async Task UninstallPluginAsync(Ulid pluginId, CancellationToken ct = default)
    {
        if (!_registry.TryRemove(pluginId, out LoadedPlugin? loaded))
        {
            throw new InvalidOperationException($"Plugin {pluginId} is not installed.");
        }

        _releaseScheduledWork?.Invoke(pluginId);

        loaded.Instance?.Dispose();

        if (loaded.LoadContext is not null)
        {
            // Tracked before the unload is asked for, so whether it actually
            // went can be answered later by looking.
            _assemblyTracker?.TrackUnload(pluginId, loaded.Info.AssemblyPath);
            loaded.LoadContext.Unload();
        }

        PluginLifecycle.Transition(loaded.Info, PluginStatus.Deleted);

        if (loaded.Info.AssemblyPath is not null)
        {
            string? pluginDir = Path.GetDirectoryName(loaded.Info.AssemblyPath);
            if (pluginDir is not null && _storage.Exists(pluginDir))
            {
                await DeleteOrQueueForDeletionAsync(pluginDir, ct);
            }
        }

        await _eventBus.PublishAsync(
            new PluginDisabledEvent
            {
                PluginId = pluginId.ToString(),
                PluginName = loaded.Info.Name,
                Uninstalled = true,
            },
            ct
        );
    }

    /// <summary>
    /// Deletes an uninstalled plugin's directory, waiting out the same
    /// just-unloaded-assembly race an update swap does. An uninstall must
    /// never leave the folder behind: the plugin is already gone from the
    /// registry and the owner's list the moment this method is called, so a
    /// delete that quietly gives up would leave disk space, and the plugin's
    /// own files, orphaned forever with nothing left in the running process
    /// that will ever look at them again.
    /// <para>
    /// When the wait budget runs out anyway, the directory is moved out from
    /// under its own name into <see cref="PluginManager.PendingDeletesFolder"/>
    /// instead — getting it off the installed list's disk footprint
    /// immediately — and the next boot's <c>ResolvePendingDeletes</c> finishes
    /// the delete for real, at the one moment nothing in the process can still
    /// be holding it.
    /// </para>
    /// </summary>
    private async Task DeleteOrQueueForDeletionAsync(string pluginDir, CancellationToken ct)
    {
        if (
            await PluginFileRetry.TryAsync(
                () => _storage.DeleteDirectory(pluginDir, recursive: true),
                ct
            )
        )
        {
            return;
        }

        string quarantine = _storage.CombinePath(
            _pluginsPath,
            PluginManager.PendingDeletesFolder,
            $"{Path.GetFileName(pluginDir)}-{Ulid.NewUlid()}"
        );

        try
        {
            string? parent = Path.GetDirectoryName(quarantine);
            if (parent is not null && !_storage.Exists(parent))
            {
                _storage.CreateDirectory(parent);
            }

            _storage.MoveDirectory(pluginDir, quarantine);

            _logger.LogWarning(
                "Plugin directory {PluginDir} was still locked; moved it aside for deletion on the next start.",
                pluginDir
            );
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked against a move too — genuinely nothing left to try from
            // inside this process. The next boot's directory scan does not
            // recognise this folder as a plugin (its manifest id is already
            // Deleted in nobody's registry), so it stays inert rather than
            // reappearing as an installed plugin; a future disk-space audit is
            // the backstop for this exceedingly rare case.
            _logger.LogWarning(
                ex,
                "Could not delete or relocate plugin directory {PluginDir}. Files are still locked.",
                pluginDir
            );
        }
    }
}
