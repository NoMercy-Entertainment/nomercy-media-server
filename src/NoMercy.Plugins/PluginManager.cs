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

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using NoMercy.Events;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;
using NoMercy.Plugins.Hub;
using NoMercy.Plugins.Verification;
using NoMercy.Storage;

namespace NoMercy.Plugins;

public class PluginManager : IPluginManager, IDisposable
{
    private readonly IEventBus _eventBus;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PluginManager> _logger;
    private readonly string _pluginsPath;
    private readonly IStorage _storage;
    private readonly IStorageDriver _driver;
    private readonly IPluginVerifier _verifier;
    private readonly IPluginConsentService _consentService;
    private readonly IPluginRegistry _registry;
    private readonly PluginLoader _loader;
    private readonly PluginLifecycleManager _lifecycle;

    // Held because an install has to know whether the copy it would replace is
    // still resident, and the answer decides whether the update lands now or on
    // the next start.
    private readonly IPluginAssemblyTracker? _assemblyTracker;

    // A fresh install and a hot-swapped update both reload a plugin directly
    // here rather than through PluginLifecycleManager.EnablePluginAsync, so
    // each needs its own call to bring a scheduled-task plugin's cron work
    // back — otherwise it stays Active with nothing running behind it until
    // the next full server start.
    private readonly Action<Ulid>? _registerScheduledWork;

    public PluginManager(
        IEventBus eventBus,
        IServiceProvider serviceProvider,
        ILogger<PluginManager> logger,
        string pluginsPath,
        IStorage storage,
        IStorageDriver driver,
        IPluginVerifier? verifier = null,
        IPluginConsentService? consentService = null,
        IPluginContextFactory? contextFactory = null,
        PluginHostOptions? hostOptions = null,
        IPluginAssemblyTracker? assemblyTracker = null,
        Action<Ulid>? releaseScheduledWork = null,
        Action<Ulid>? registerScheduledWork = null
    )
    {
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _serviceProvider =
            serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _pluginsPath = pluginsPath ?? throw new ArgumentNullException(nameof(pluginsPath));
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _verifier = verifier ?? new PluginVerifier();
        _consentService =
            consentService
            ?? new PluginConsentService(
                new ConfigPluginConsentStore(
                    new PluginConfiguration(
                        _storage.CombinePath(_pluginsPath, "data", "platform"),
                        _storage
                    )
                )
            );
        _registry = new PluginRegistry();
        _assemblyTracker = assemblyTracker;
        _registerScheduledWork = registerScheduledWork;

        // Built here only when DI did not supply one, which is the test and
        // direct-construction path. Its protector is ephemeral, so a secret
        // written through it does not survive a restart — that is the safe
        // failure, because the alternative default is a secret on disk in the
        // clear. The server's registration always passes the real factory.
        IPluginContextFactory factory =
            contextFactory
            ?? new PluginContextFactory(
                _eventBus,
                _serviceProvider,
                _storage,
                new ConfigPluginGrantStore(PlatformConfiguration()),
                new EphemeralDataProtectionProvider(),
                new NullPluginLibraryQuery(),
                new NullPluginLibraryWriterFactory(),
                PlatformConfiguration(),
                new NullPluginHubContextFactory()
            );

        _loader = new(
            _eventBus,
            _serviceProvider,
            _logger,
            _pluginsPath,
            _storage,
            _registry,
            _verifier,
            _consentService,
            factory,
            hostOptions
        );
        _lifecycle = new(
            _eventBus,
            _serviceProvider,
            _logger,
            _pluginsPath,
            _storage,
            _registry,
            _loader,
            factory,
            assemblyTracker,
            releaseScheduledWork,
            registerScheduledWork
        );
    }

    private PluginConfiguration PlatformConfiguration() =>
        new(_storage.CombinePath(_pluginsPath, "data", "platform"), _storage);

    public IReadOnlyList<PluginInfo> GetInstalledPlugins()
    {
        return _registry.Values.Select(lp => lp.Info).ToList().AsReadOnly();
    }

    public Task InstallPluginAsync(string packagePath, CancellationToken ct = default)
    {
        return InstallPluginAsync(packagePath, expectedChecksum: null, ct);
    }

    public async Task InstallPluginAsync(
        string packagePath,
        string? expectedChecksum,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);

        string fullPath = Path.GetFullPath(packagePath);

        // Source assembly may be anywhere on disk (user-supplied install path).
        // Use the raw backend for the existence check and copy-in; the destination
        // is always inside the allowlisted plugin root so _storage covers it.
        if (!_driver.FileExists(fullPath))
        {
            throw new FileNotFoundException($"Plugin assembly not found: {fullPath}", fullPath);
        }

        if (!string.IsNullOrWhiteSpace(expectedChecksum))
        {
            // This install path receives a bare assembly with no plugin.json
            // alongside it, so ABI cannot be judged here (TargetAbi stays null
            // and that stage passes by design); only the checksum a repository
            // caller supplies is enforced, before anything is copied to disk.
            PluginManifest checksumManifest = new()
            {
                Id = Ulid.Empty,
                Name = Path.GetFileNameWithoutExtension(fullPath),
                Description = string.Empty,
                Version = "0.0.0",
                Assembly = Path.GetFileName(fullPath),
            };

            PluginVerificationResult verification = _verifier.Verify(
                checksumManifest,
                fullPath,
                expectedChecksum
            );

            if (!verification.Verified)
            {
                throw new PluginVerificationException(
                    $"Plugin '{checksumManifest.Name}' failed verification: {string.Join("; ", verification.Failures)}"
                );
            }
        }

        string pluginName = Path.GetFileNameWithoutExtension(fullPath);
        string pluginDir = _storage.CombinePath(_pluginsPath, pluginName);

        if (!await _storage.ExistsAsync(pluginDir, ct))
        {
            await _storage.CreateDirectoryAsync(pluginDir, ct);
        }

        string destPath = _storage.CombinePath(pluginDir, Path.GetFileName(fullPath));

        // The destination may be a running plugin's own assembly: on Windows a
        // loaded ALC keeps its file open, so the copy below fails with an
        // IOException/UnauthorizedAccessException rather than a missing-file
        // error. That used to reach the caller as a raw 422 stack trace, or -
        // once the archive path learned to stage instead - always fell back to
        // waiting for the next boot. Now the resident copy is unloaded and
        // swapped live, the same as an archive update; staging for next start
        // is only what happens when the assembly genuinely will not let go
        // inside the wait budget.
        try
        {
            _driver.CopyFile(fullPath, destPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ulid? residentId = _registry
                .Values.FirstOrDefault(loaded =>
                    loaded.Info.AssemblyPath is not null
                    && _driver
                        .GetFullPath(loaded.Info.AssemblyPath)
                        .Equals(_driver.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase)
                )
                ?.Info.Id;

            if (
                residentId is { } pluginId
                && await TrySwapResidentAssemblyAsync(pluginId, fullPath, destPath, ct)
            )
            {
                await LoadPluginAssemblyAsync(destPath, ct);
                _registerScheduledWork?.Invoke(pluginId);
                return;
            }

            string staging = _storage.CombinePath(_pluginsPath, PendingUpdatesFolder, pluginName);

            if (_driver.DirectoryExists(staging))
            {
                _driver.DeleteDirectory(staging, recursive: true);
            }

            _driver.CreateDirectory(staging);
            _driver.CopyFile(
                fullPath,
                _storage.CombinePath(staging, Path.GetFileName(fullPath)),
                overwrite: true
            );

            _logger.LogInformation(
                "Plugin update for {Folder} is staged: its assembly is still loaded, so it is applied on the next start.",
                pluginName
            );

            throw new PluginUpdatePendingRestartException(pluginName);
        }

        await LoadPluginAssemblyAsync(destPath, ct);
    }

    /// <summary>
    /// The single-assembly twin of <see cref="TrySwapResidentPluginAsync"/>:
    /// unloads the resident plugin, backs its one file up beside itself, copies
    /// the new one over it, and restores the backup if the copy - or a caller
    /// - reports the result as bad. Returns false, having changed nothing,
    /// when the file does not free up inside <see cref="UnloadWaitBudget"/>.
    /// </summary>
    private async Task<bool> TrySwapResidentAssemblyAsync(
        Ulid pluginId,
        string sourcePath,
        string destPath,
        CancellationToken ct
    )
    {
        bool wasLoaded = await _lifecycle.UnloadForUpdateAsync(pluginId, ct);

        string rollbackPath = destPath + RollbackSuffix;

        if (_driver.FileExists(rollbackPath))
        {
            _driver.DeleteFile(rollbackPath);
        }

        if (!await MoveWhenFreedAsync(() => _driver.MoveFile(destPath, rollbackPath), ct))
        {
            // Still locked. Nothing moved, so reload what is already there and
            // let the caller fall back to staging for the next start.
            if (wasLoaded)
            {
                await LoadPluginAssemblyAsync(destPath, ct);
                _registerScheduledWork?.Invoke(pluginId);
            }

            return false;
        }

        try
        {
            _driver.CopyFile(sourcePath, destPath, overwrite: true);
        }
        catch (Exception)
        {
            _driver.MoveFile(rollbackPath, destPath);

            if (wasLoaded)
            {
                await LoadPluginAssemblyAsync(destPath, ct);
                _registerScheduledWork?.Invoke(pluginId);
            }

            throw;
        }

        try
        {
            _driver.DeleteFile(rollbackPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                "Could not remove the rollback copy for {DestPath} yet; it will be cleared on the next start.",
                destPath
            );
        }

        return true;
    }

    /// <summary>Suffix a backed-up assembly carries while an update is in flight.</summary>
    internal const string RollbackSuffix = ".rollback";

    public async Task InstallPluginArchiveAsync(
        string archivePath,
        string? expectedChecksum = null,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        string fullPath = Path.GetFullPath(archivePath);

        if (!_driver.FileExists(fullPath))
        {
            throw new FileNotFoundException($"Plugin archive not found: {fullPath}", fullPath);
        }

        // Before a single byte is unpacked. An archive that fails here must never
        // have existed on disk anywhere the loader looks.
        if (!string.IsNullOrWhiteSpace(expectedChecksum))
        {
            string actual = await ComputeSha256Async(fullPath, ct);

            if (!actual.Equals(expectedChecksum.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new PluginVerificationException(
                    $"Plugin archive failed verification: expected checksum {expectedChecksum}, got {actual}"
                );
            }
        }

        await using ZipArchive archive = await ZipFile.OpenReadAsync(fullPath, ct);

        PluginManifestEntry manifest = FindManifest(archive, fullPath);
        string pluginDir = _storage.CombinePath(_pluginsPath, manifest.FolderName);
        string staging = _storage.CombinePath(
            _pluginsPath,
            PendingUpdatesFolder,
            manifest.FolderName
        );

        // Unpacked beside the installed copy, never over it. Extraction writes
        // one entry at a time, so anything that fails part way through used to
        // leave the folder holding some of the new plugin and some of the old.
        // That state is worse than a refusal: the manifest would name one
        // version while the assembly actually running is another, and a
        // catalogue comparing versions would call it up to date and never offer
        // the update again.
        if (_driver.DirectoryExists(staging))
        {
            _driver.DeleteDirectory(staging, recursive: true);
        }

        _driver.CreateDirectory(staging);

        await ExtractAsync(archive, manifest, staging, ct);

        // A resident plugin is unloaded and swapped live: the owner asked for an
        // update, not a server restart, and the assembly lock that used to force
        // one only lasts for the moment between Unload() and the GC actually
        // freeing the file. If that moment runs longer than we are willing to
        // wait for, the staged copy is left for the next start instead of
        // failing the request.
        if (IsResident(manifest.Id))
        {
            if (await TrySwapResidentPluginAsync(manifest.Id, staging, pluginDir, ct))
            {
                return;
            }

            _logger.LogInformation(
                "Plugin update for {Folder} is staged: its assembly is still loaded, so it is applied on the next start.",
                manifest.FolderName
            );

            throw new PluginUpdatePendingRestartException(manifest.FolderName);
        }

        ApplyStaged(staging, pluginDir);

        await LoadPluginFromManifestAsync(_storage.CombinePath(pluginDir, "plugin.json"), ct);

        _registerScheduledWork?.Invoke(manifest.Id);
    }

    /// <summary>
    /// Where a plugin folder waits while it is being replaced, so an update
    /// that fails partway through can put it back exactly as it was.
    /// </summary>
    internal const string RollbackFolder = ".rollback";

    /// <summary>
    /// Where an uninstalled plugin's directory waits when it could not be
    /// deleted immediately because its assembly was still locked. Not the same
    /// folder an update rolls back from: nothing here is coming back — the
    /// next boot's <see cref="ResolvePendingDeletes"/> deletes every entry
    /// unconditionally, at the one moment nothing in the process can still be
    /// holding it. Skipped by the boot scan for the same reason
    /// <see cref="RollbackFolder"/> and <see cref="PendingUpdatesFolder"/> are.
    /// </summary>
    internal const string PendingDeletesFolder = ".pending-deletes";

    /// <summary>
    /// How long to wait for a just-unloaded assembly to actually let go of its
    /// files before giving up and staging the update for the next start
    /// instead.
    /// </summary>
    private static readonly TimeSpan UnloadWaitBudget = PluginFileRetry.DefaultBudget;

    /// <summary>
    /// Unloads a resident plugin, moves its installed folder aside, moves the
    /// staged update into its place, and reloads it — rolling the folder back
    /// and reloading the old copy if any of that does not finish cleanly.
    /// Returns false, having changed nothing, when the assembly does not free
    /// its files inside <see cref="UnloadWaitBudget"/>.
    /// </summary>
    private async Task<bool> TrySwapResidentPluginAsync(
        Ulid pluginId,
        string staging,
        string pluginDir,
        CancellationToken ct
    )
    {
        bool wasLoaded = await _lifecycle.UnloadForUpdateAsync(pluginId, ct);
        string manifestPath = _storage.CombinePath(pluginDir, "plugin.json");

        string rollbackDir = _storage.CombinePath(
            _pluginsPath,
            RollbackFolder,
            Path.GetFileName(pluginDir)
        );

        if (_driver.DirectoryExists(rollbackDir))
        {
            _driver.DeleteDirectory(rollbackDir, recursive: true);
        }

        // Directory.Move (and its remote-driver equivalents) refuses when the
        // destination's own parent does not exist yet - true on every very
        // first update this server ever applies, since nothing else has a
        // reason to create .rollback before this does. Without this, that
        // move always failed, indistinguishable from "still locked" because
        // DirectoryNotFoundException is itself an IOException - so the
        // fallback fired on every attempt and the swap could never go hot.
        string rollbackRoot = _storage.CombinePath(_pluginsPath, RollbackFolder);
        if (!_driver.DirectoryExists(rollbackRoot))
        {
            _driver.CreateDirectory(rollbackRoot);
        }

        bool hadExisting = _driver.DirectoryExists(pluginDir);

        if (
            hadExisting
            && !await MoveWhenFreedAsync(() => _driver.MoveDirectory(pluginDir, rollbackDir), ct)
        )
        {
            // Still locked after the wait budget. Nothing has moved, so the
            // installed copy is exactly as it was; reload it if we unloaded it,
            // and let the caller fall back to staging the update for next start.
            if (wasLoaded)
            {
                await LoadPluginFromManifestAsync(manifestPath, ct);
                _registerScheduledWork?.Invoke(pluginId);
            }

            return false;
        }

        try
        {
            ApplyStaged(staging, pluginDir);

            // Via the manifest, not the bare assembly: the manifest may have
            // changed (version, capabilities, name) along with the code, and a
            // reload that ignored it would report the update as applied while
            // showing the owner the old metadata.
            await LoadPluginFromManifestAsync(manifestPath, ct);
            _registerScheduledWork?.Invoke(pluginId);

            return true;
        }
        catch (Exception)
        {
            // The new copy did not come out right - put the old one back and
            // load that instead, so a bad update leaves the plugin exactly as
            // it was rather than gone.
            if (_driver.DirectoryExists(pluginDir))
            {
                _driver.DeleteDirectory(pluginDir, recursive: true);
            }

            if (hadExisting)
            {
                _driver.MoveDirectory(rollbackDir, pluginDir);
                PruneIfEmpty(rollbackRoot);

                if (wasLoaded)
                {
                    await LoadPluginFromManifestAsync(manifestPath, ct);
                    _registerScheduledWork?.Invoke(pluginId);
                }
            }

            // This attempt is being reported as a real failure, not staged for
            // a later retry that would only fail the same way again - so the
            // half-applied staging copy is junk now, not a queued update.
            if (_driver.DirectoryExists(staging))
            {
                try
                {
                    DeleteAndPruneEmptyParent(staging, PendingUpdatesFolder);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(
                        "Could not remove the failed update's staged copy at {Staging}; it will be cleared on the next start.",
                        staging
                    );
                }
            }

            throw;
        }
        finally
        {
            // Best-effort: Windows can rename a just-unloaded assembly's folder
            // but not always delete it in the same instant. Letting that throw
            // here would report a working update as failed and send the caller
            // into a rollback it does not need; the next boot's
            // ResolvePendingRollbacksAsync clears anything left behind.
            if (hadExisting && _driver.DirectoryExists(rollbackDir))
            {
                try
                {
                    _driver.DeleteDirectory(rollbackDir, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(
                        "Could not remove the rollback copy for {PluginDir} yet; it will be cleared on the next start.",
                        pluginDir
                    );
                }
            }
        }
    }

    /// <summary>
    /// Retries a move while it fails on a file still locked by a just-unloaded
    /// assembly context — see <see cref="PluginFileRetry"/>. False, unchanged,
    /// once <see cref="UnloadWaitBudget"/> runs out.
    /// </summary>
    private Task<bool> MoveWhenFreedAsync(Action move, CancellationToken ct)
    {
        return PluginFileRetry.TryAsync(move, ct, UnloadWaitBudget);
    }

    /// <summary>
    /// Where an update waits when the copy it replaces is still loaded.
    ///
    /// Inside the plugins folder rather than in temp, because a pending update
    /// has to survive the shutdown it is waiting for, and temp does not have to.
    /// The boot scan skips it by name for the same reason it skips
    /// <c>configurations</c> and <c>data</c>: it holds plugins that are not
    /// installed yet, and loading one from here would run a version the rest of
    /// the server does not know about.
    /// </summary>
    internal const string PendingUpdatesFolder = ".pending-updates";

    /// <summary>
    /// Whether this plugin's assembly is loaded in this process right now.
    ///
    /// Two questions, because they fail the same way and neither alone covers
    /// it: the registry answers for a plugin that is loaded, and the tracker
    /// answers for one that has been unloaded and whose files are still held —
    /// which is the case a best-effort unload leaves behind.
    /// </summary>
    private bool IsResident(Ulid pluginId) =>
        _registry.TryGetValue(pluginId, out _)
        || (_assemblyTracker?.IsStillLoaded(pluginId) ?? false);

    /// <summary>
    /// Moves a staged plugin into place, replacing what is there.
    ///
    /// Only ever called once the assembly is known to be replaceable, so this
    /// is the point where the update becomes visible and there is nothing to
    /// roll back.
    /// </summary>
    /// <summary>
    /// Test seam: overrides the per-file copy <see cref="ApplyStaged"/>
    /// performs while applying a staged update. Null in production, where the
    /// real copy always runs — this exists so a test can force a hot swap to
    /// fail partway through applying its staged copy (a real disk fault, an
    /// unreadable file) without needing to reproduce that fault on a real
    /// filesystem, which is otherwise indistinguishable from the earlier
    /// lock-detection retry that a real locked file trips first.
    /// </summary>
    internal Action<Stream, Stream>? CopyStreamOverride { get; set; }

    private void ApplyStaged(string staging, string pluginDir)
    {
        if (!_driver.DirectoryExists(pluginDir))
        {
            _driver.CreateDirectory(pluginDir);
        }

        // Through the driver rather than IStorage: a StorageEntry's path is
        // relative to the storage scope, and the two roots here are real paths.
        // Mixing the two produced a destination full of `..` segments that the
        // path guard refused - correctly.
        foreach (
            StorageEntryInfo info in _driver.EnumerateEntries(
                staging,
                "*",
                SearchOption.AllDirectories
            )
        )
        {
            if (info.IsDirectory)
            {
                continue;
            }

            string relative = Path.GetRelativePath(staging, info.Path);
            string destination = _storage.CombinePath(pluginDir, relative);
            string? parent = Path.GetDirectoryName(destination);

            if (parent is not null && !_driver.DirectoryExists(parent))
            {
                _driver.CreateDirectory(parent);
            }

            using Stream source = _driver.OpenRead(info.Path);
            using Stream target = _driver.OpenWrite(destination, overwrite: true);

            if (CopyStreamOverride is not null)
            {
                CopyStreamOverride(source, target);
            }
            else
            {
                source.CopyTo(target);
            }
        }

        DeleteAndPruneEmptyParent(staging, PendingUpdatesFolder);
    }

    /// <summary>
    /// Deletes a folder waiting under one of the marker roots
    /// (<see cref="PendingUpdatesFolder"/>, <see cref="RollbackFolder"/>), then
    /// removes that root too once the last entry under it is gone. An empty
    /// marker folder sitting in the plugins directory reads like something is
    /// still queued when nothing is.
    /// </summary>
    private void DeleteAndPruneEmptyParent(string entry, string markerRootName)
    {
        _driver.DeleteDirectory(entry, recursive: true);

        PruneIfEmpty(_storage.CombinePath(_pluginsPath, markerRootName));
    }

    /// <summary>
    /// Removes <paramref name="root"/> if it exists and has nothing left in
    /// it. An empty marker folder sitting in the plugins directory reads like
    /// something is still queued when nothing is - shared by every path that
    /// can be the one to take a marker root's last entry (applying a staged
    /// update, resolving a rollback, restoring one after a failed swap).
    /// </summary>
    private void PruneIfEmpty(string root)
    {
        if (
            _driver.DirectoryExists(root)
            && !_driver.EnumerateEntries(root, "*", SearchOption.TopDirectoryOnly).Any()
        )
        {
            _driver.DeleteDirectory(root, recursive: false);
        }
    }

    /// <summary>
    /// Applies every update that was waiting on this restart.
    ///
    /// Before anything is loaded, which is the whole point: this is the one
    /// moment in the process's life when no plugin assembly is held and the
    /// files can be replaced. A single failure is logged and skipped rather
    /// than thrown, because one plugin that cannot be updated must not stop the
    /// server from starting the others.
    /// </summary>
    private void ApplyPendingUpdates()
    {
        ProcessTopLevelFolders(
            _storage.CombinePath(_pluginsPath, PendingUpdatesFolder),
            "staged update",
            (path, folderName) =>
            {
                ApplyStaged(path, _storage.CombinePath(_pluginsPath, folderName));

                _logger.LogInformation("Applied the staged update for {Folder}.", folderName);
            }
        );
    }

    /// <summary>
    /// Resolves every folder left in <see cref="RollbackFolder"/> by a hot
    /// update that never reached its own cleanup — a crash between the old
    /// folder being moved aside and the swap finishing.
    ///
    /// A rollback folder whose plugin directory is now missing is the only
    /// copy that plugin has left, so it goes back. One whose plugin directory
    /// is there belongs to an update that completed before the crash, so it is
    /// discarded. Getting that the other way round would silently downgrade a
    /// plugin on every boot that happens to land badly.
    /// </summary>
    private void ResolvePendingRollbacks()
    {
        ProcessTopLevelFolders(
            _storage.CombinePath(_pluginsPath, RollbackFolder),
            "rollback",
            (path, folderName) =>
            {
                string pluginDir = _storage.CombinePath(_pluginsPath, folderName);

                if (_driver.DirectoryExists(pluginDir))
                {
                    _driver.DeleteDirectory(path, recursive: true);
                    return;
                }

                _driver.MoveDirectory(path, pluginDir);

                _logger.LogWarning(
                    "Restored {Folder} from an interrupted update; it was left in {Rollback} by a crash mid-swap.",
                    [folderName, RollbackFolder]
                );
            }
        );
    }

    /// <summary>
    /// Deletes every directory an uninstall could not remove immediately
    /// because its assembly was still locked. By boot time the process that
    /// held it is gone, so nothing here can still be resisting deletion — an
    /// uninstall must never leave a plugin's files behind forever, and this is
    /// where the wait budget's rare loss gets made good on.
    /// </summary>
    private void ResolvePendingDeletes()
    {
        ProcessTopLevelFolders(
            _storage.CombinePath(_pluginsPath, PendingDeletesFolder),
            "pending delete",
            (path, folderName) =>
            {
                _driver.DeleteDirectory(path, recursive: true);

                _logger.LogInformation(
                    "Deleted {Folder}, left behind by an uninstall while its files were locked.",
                    folderName
                );
            }
        );
    }

    /// <summary>
    /// The bare-assembly install's twin of <see cref="ResolvePendingRollbacks"/>:
    /// resolves every <c>*.rollback</c> file a single-dll hot update could not
    /// clean up after itself. A backup whose original file is back in place
    /// belonged to an update that completed, so it is discarded; one whose
    /// original is missing is the only copy of that assembly left, so it is
    /// restored.
    /// </summary>
    private void ResolveStaleAssemblyBackups()
    {
        if (!_driver.DirectoryExists(_pluginsPath))
        {
            return;
        }

        foreach (
            StorageEntryInfo entry in _driver
                .EnumerateEntries(_pluginsPath, "*" + RollbackSuffix, SearchOption.AllDirectories)
                .ToList()
        )
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            string original = entry.Path[..^RollbackSuffix.Length];

            try
            {
                if (_driver.FileExists(original))
                {
                    _driver.DeleteFile(entry.Path);
                    continue;
                }

                _driver.MoveFile(entry.Path, original);

                _logger.LogWarning(
                    "Restored {Original} from an interrupted update; its backup was left behind by a crash mid-swap.",
                    original
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not resolve the stale backup {Backup}.", entry.Path);
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="process"/> against each top-level folder directly
    /// under <paramref name="root"/>, logging and skipping one that throws
    /// rather than letting it stop the rest — the shape both a boot-time apply
    /// and a boot-time rollback need, since neither may let one bad plugin
    /// folder block every other one from starting.
    /// </summary>
    private void ProcessTopLevelFolders(string root, string label, Action<string, string> process)
    {
        if (!_driver.DirectoryExists(root))
        {
            return;
        }

        foreach (
            StorageEntryInfo entry in _driver
                .EnumerateEntries(root, "*", SearchOption.TopDirectoryOnly)
                .ToList()
        )
        {
            if (!entry.IsDirectory)
            {
                continue;
            }

            string folderName = Path.GetFileName(entry.Path);

            try
            {
                process(entry.Path, folderName);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Could not resolve the {Label} entry for {Folder}.",
                    [label, folderName]
                );
            }
        }

        PruneIfEmpty(root);
    }

    /// <summary>
    /// The manifest decides what the archive is. Located rather than assumed:
    /// a plugin is published either as its folder or as the folder's contents,
    /// and both are the same plugin.
    /// </summary>
    private static PluginManifestEntry FindManifest(ZipArchive archive, string archivePath)
    {
        ZipArchiveEntry? entry = archive
            .Entries.Where(candidate =>
                Path.GetFileName(candidate.FullName)
                    .Equals("plugin.json", StringComparison.OrdinalIgnoreCase)
            )
            // Shallowest wins, so a plugin that ships its own docs folder
            // containing an example manifest cannot outrank the real one.
            .MinBy(candidate => candidate.FullName.Count(ArchiveSeparators.Contains));

        if (entry is null)
        {
            throw new PluginVerificationException(
                $"Plugin archive has no plugin.json: {Path.GetFileName(archivePath)}"
            );
        }

        string prefix = entry.FullName[..(entry.FullName.Length - "plugin.json".Length)];
        PluginManifest parsed;

        using (Stream stream = entry.Open())
        using (StreamReader reader = new(stream))
        {
            parsed =
                PluginManifestParser.Parse(reader.ReadToEnd())
                ?? throw new PluginVerificationException(
                    $"Plugin archive has an unreadable plugin.json: {Path.GetFileName(archivePath)}"
                );
        }

        if (string.IsNullOrWhiteSpace(parsed.Assembly))
        {
            throw new PluginVerificationException(
                "Plugin manifest does not name an assembly, so there is nothing to load."
            );
        }

        return new(
            prefix,
            parsed.Assembly,
            Path.GetFileNameWithoutExtension(parsed.Assembly),
            parsed.Id
        );
    }

    private async Task<string> ExtractAsync(
        ZipArchive archive,
        PluginManifestEntry manifest,
        string pluginDir,
        CancellationToken ct
    )
    {
        string root = Path.GetFullPath(pluginDir);
        string? assemblyPath = null;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (ArchiveSeparators.Contains(entry.FullName[^1]))
            {
                continue;
            }

            if (!entry.FullName.StartsWith(manifest.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string relative = entry.FullName[manifest.Prefix.Length..];
            string destination = Path.GetFullPath(_storage.CombinePath(root, relative));

            // The archive names its own entries, so an entry may name a path.
            // Resolve first and refuse anything that lands outside the plugin's
            // own folder, or a zip writes wherever it likes on this machine.
            if (
                !destination.StartsWith(
                    root + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal
                )
            )
            {
                throw new PluginVerificationException(
                    $"Plugin archive tried to write outside its folder: {entry.FullName}"
                );
            }

            string? parent = Path.GetDirectoryName(destination);
            if (parent is not null && !_storage.Exists(parent))
            {
                _storage.CreateDirectory(parent);
            }

            await using (Stream source = entry.Open())
            await using (Stream target = _driver.OpenWrite(destination, overwrite: true))
            {
                await source.CopyToAsync(target, ct);
            }

            if (
                Path.GetFileName(destination)
                    .Equals(manifest.AssemblyFileName, StringComparison.OrdinalIgnoreCase)
            )
            {
                assemblyPath = destination;
            }
        }

        return assemblyPath
            ?? throw new PluginVerificationException(
                $"Plugin archive does not contain the assembly its manifest names: {manifest.AssemblyFileName}"
            );
    }

    private async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using Stream stream = _driver.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, ct);

        return Convert.ToHexStringLower(hash);
    }

    // Zip entries name their own separator and a Windows-built archive uses the
    // other one, so both count regardless of the host this runs on.
    private static readonly char[] ArchiveSeparators = ['/', '\\'];

    private sealed record PluginManifestEntry(
        string Prefix,
        string AssemblyFileName,
        string FolderName,
        Ulid Id
    );

    public Task EnablePluginAsync(Ulid pluginId, CancellationToken ct = default)
    {
        return _lifecycle.EnablePluginAsync(pluginId, ct);
    }

    public Task DisablePluginAsync(Ulid pluginId, CancellationToken ct = default)
    {
        return _lifecycle.DisablePluginAsync(pluginId, ct);
    }

    public Task RestartPluginAsync(Ulid pluginId, CancellationToken ct = default)
    {
        return _lifecycle.RestartPluginAsync(pluginId, ct);
    }

    public Task UninstallPluginAsync(Ulid pluginId, CancellationToken ct = default)
    {
        return _lifecycle.UninstallPluginAsync(pluginId, ct);
    }

    public async Task LoadPluginsFromDirectoryAsync(CancellationToken ct = default)
    {
        if (!_storage.Exists(_pluginsPath))
        {
            return;
        }

        // Before the scan below, because this is the one moment no plugin
        // assembly is held and a staged update can replace the files it needs to.
        ApplyPendingUpdates();

        // Same moment, for a hot swap that crashed before its own cleanup ran.
        ResolvePendingRollbacks();

        // And for an uninstall that could not delete its own directory while
        // the server was still running.
        ResolvePendingDeletes();

        // And for a single-dll hot update that could not clean up its own
        // backup file.
        ResolveStaleAssemblyBackups();

        IReadOnlyList<StorageEntry> entries = _storage.List(_pluginsPath, null, recursive: false);
        foreach (StorageEntry entry in entries)
        {
            if (!entry.IsDirectory)
            {
                continue;
            }

            string pluginDir = entry.Path;
            string dirName = Path.GetFileName(pluginDir);
            if (
                dirName
                is "configurations"
                    or "data"
                    or PendingUpdatesFolder
                    or RollbackFolder
                    or PendingDeletesFolder
            )
            {
                continue;
            }

            try
            {
                string manifestPath = _storage.CombinePath(pluginDir, "plugin.json");
                if (_storage.Exists(manifestPath))
                {
                    await LoadPluginFromManifestAsync(manifestPath, ct);
                    continue;
                }

                IReadOnlyList<StorageEntry> dllEntries = _storage.List(
                    pluginDir,
                    "*.dll",
                    recursive: false
                );
                foreach (StorageEntry dllEntry in dllEntries)
                {
                    if (!dllEntry.IsDirectory)
                    {
                        await LoadPluginAssemblyAsync(dllEntry.Path, ct);
                    }
                }
            }
            catch (Exception ex)
            {
                // Defense in depth: the load helpers already isolate their own
                // failures, but an unexpected throw must not stop the remaining
                // plugin directories from being scanned.
                _logger.LogError(
                    ex,
                    "Unexpected failure while loading plugin directory {PluginDir}; skipping it.",
                    pluginDir
                );
            }
        }
    }

    public async Task<IReadOnlyList<PluginLoadResult>> LoadAllAsync(CancellationToken ct = default)
    {
        if (!_storage.Exists(_pluginsPath))
        {
            _logger.LogInformation(
                "Plugins directory missing: {Path}. No plugins loaded.",
                _pluginsPath
            );
            return [];
        }

        await LoadPluginsFromDirectoryAsync(ct);

        List<PluginLoadResult> results = [];
        foreach (LoadedPlugin loaded in _registry.Values)
        {
            if (loaded.Instance is not null && loaded.Info.Status == PluginStatus.Active)
            {
                results.Add(
                    new(
                        loaded.Info.Id,
                        loaded.Info.Name,
                        loaded.Info.Version.ToString(),
                        loaded.Instance
                    )
                );
            }
        }

        return results;
    }

    internal Task LoadPluginFromManifestAsync(string manifestPath, CancellationToken ct = default)
    {
        return _loader.LoadPluginFromManifestAsync(manifestPath, ct);
    }

    internal Task LoadPluginAssemblyAsync(string assemblyPath, CancellationToken ct = default)
    {
        return _loader.LoadPluginAssemblyAsync(assemblyPath, ct);
    }

    public IPlugin? GetPluginInstance(Ulid pluginId)
    {
        if (_registry.TryGetValue(pluginId, out LoadedPlugin? loaded))
        {
            return loaded.Instance;
        }

        return null;
    }

    public PluginInfo? GetPluginInfo(Ulid pluginId) =>
        _registry.TryGetValue(pluginId, out LoadedPlugin? loaded) ? loaded.Info : null;

    public async Task<Dictionary<string, string>?> ReadTranslationsAsync(
        Ulid pluginId,
        string locale,
        CancellationToken ct
    )
    {
        if (!_registry.TryGetValue(pluginId, out LoadedPlugin? loaded))
            return null;

        string? manifestPath = loaded.Info.ManifestPath;
        if (string.IsNullOrWhiteSpace(manifestPath) || !_storage.Exists(manifestPath))
            return null;

        PluginTranslations? declared;
        try
        {
            declared = JsonSerializer
                .Deserialize<PluginManifest>(
                    await _storage.ReadAllTextAsync(manifestPath, ct),
                    TranslationJson
                )
                ?.Translations;
        }
        catch (JsonException)
        {
            return null;
        }

        if (declared is null)
            return null;

        string? root = Path.GetDirectoryName(manifestPath);
        if (root is null)
            return null;

        // Falls back to the locale the plugin was authored in. A viewer whose
        // language a plugin does not ship should read it in the language it was
        // written in, never in empty labels.
        string wanted = declared.Locales.Contains(locale) ? locale : declared.Source;

        return await ReadLocaleFileAsync(_storage.CombinePath(root, declared.Path), wanted, ct);
    }

    private static readonly JsonSerializerOptions TranslationJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private async Task<Dictionary<string, string>?> ReadLocaleFileAsync(
        string directory,
        string locale,
        CancellationToken ct
    )
    {
        string path = _storage.CombinePath(directory, $"{locale}.json");

        if (!_storage.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(
                await _storage.ReadAllTextAsync(path, ct)
            );
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public IEnumerable<T> GetPluginsOfType<T>()
        where T : IPlugin
    {
        return
        [
            .. _registry
                .Values.Where(lp => lp is { Instance: T, Info.Status: PluginStatus.Active })
                .Select(lp => (T)lp.Instance!),
        ];
    }

    public IEnumerable<IPluginServiceRegistrator> GetServiceRegistrators()
    {
        return
        [
            .. _registry
                .Values.Where(lp =>
                    lp is { Instance: IPluginServiceRegistrator, Info.Status: PluginStatus.Active }
                )
                .Select(lp => (IPluginServiceRegistrator)lp.Instance!),
        ];
    }

    public void Dispose()
    {
        foreach (LoadedPlugin loaded in _registry.Values)
        {
            loaded.Instance?.Dispose();
            loaded.LoadContext?.Unload();
        }

        _registry.Clear();
    }
}
