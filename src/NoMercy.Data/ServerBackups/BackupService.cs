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
using NoMercy.Storage;

namespace NoMercy.Data.ServerBackups;

public sealed class BackupService(
    IStorageDriver storage,
    ISqliteFileOperations sqlite,
    BackupOptions options,
    TimeProvider clock,
    ILogger<BackupService> logger
) : IBackupService
{
    public const string DatabasesFolder = "databases";
    public const string ConfigFolder = "config";

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<BackupSetSummary> CreateAsync(
        string reason,
        CancellationToken cancellationToken
    )
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            BackupManifest manifest = await WriteSetAsync(reason, cancellationToken);
            logger.LogInformation(
                "Backup {BackupId} created ({Reason})",
                manifest.Id,
                manifest.Reason
            );
            Prune();
            return BackupSetSummary.From(manifest);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<BackupSetSummary>> ListAsync(
        CancellationToken cancellationToken
    )
    {
        List<BackupSetSummary> sets = [];
        foreach (string id in ListSetIds())
        {
            BackupManifest? manifest = await ReadManifestAsync(id, cancellationToken);
            if (manifest is not null)
                sets.Add(BackupSetSummary.From(manifest));
        }

        return sets;
    }

    private async Task<BackupManifest> WriteSetAsync(
        string reason,
        CancellationToken cancellationToken
    )
    {
        DateTime createdAt = clock.GetUtcNow().UtcDateTime;
        string id = BackupSetId.Create(createdAt);
        string building = Path.Combine(options.SetsRoot, BackupSetId.IncompletePrefix + id);
        string published = Path.Combine(options.SetsRoot, id);
        if (storage.DirectoryExists(published))
            throw new IOException($"A backup named {id} already exists.");

        BackupManifest manifest = new()
        {
            Id = id,
            CreatedAt = createdAt,
            Reason = reason,
            ServerVersion = options.ServerVersion,
        };

        storage.CreateDirectory(building);
        try
        {
            storage.CreateDirectory(Path.Combine(building, DatabasesFolder));
            foreach (BackupDatabaseFile database in options.Databases)
                manifest.Databases.Add(
                    await CopyDatabaseAsync(database, building, cancellationToken)
                );

            foreach (string relative in BackupFiles.ListFilesRelative(storage, options.ConfigRoot))
                manifest.ConfigFiles.Add(
                    await CopyConfigFileAsync(relative, building, cancellationToken)
                );

            await BackupFiles.WriteTextAtomicallyAsync(
                storage,
                Path.Combine(building, BackupManifest.FileName),
                BackupFiles.Serialize(manifest),
                cancellationToken
            );

            storage.MoveDirectory(building, published);
            return manifest;
        }
        catch
        {
            TryDeleteDirectory(building);
            throw;
        }
    }

    private async Task<BackupManifestDatabase> CopyDatabaseAsync(
        BackupDatabaseFile database,
        string building,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!storage.FileExists(database.LivePath))
            throw new FileNotFoundException(
                $"The {database.Name} database is missing, so no complete backup can be made.",
                database.LivePath
            );

        string relative = $"{DatabasesFolder}/{database.FileName}";
        string target = BackupFiles.ResolveInside(building, relative);
        sqlite.CopyOnline(database.LivePath, target);

        IReadOnlyList<string> applied = sqlite.ReadAppliedMigrations(target);
        return new()
        {
            Name = database.Name,
            Path = relative,
            Size = storage.GetFileSize(target),
            Sha256 = await BackupFiles.Sha256Async(storage, target, cancellationToken),
            LastMigration = applied.Count > 0 ? applied[^1] : null,
        };
    }

    private async Task<BackupManifestFile> CopyConfigFileAsync(
        string relativeToConfig,
        string building,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        string relative = $"{ConfigFolder}/{relativeToConfig}";
        string source = BackupFiles.ResolveInside(options.ConfigRoot, relativeToConfig);
        string target = BackupFiles.ResolveInside(building, relative);

        storage.CreateDirectory(Path.GetDirectoryName(target)!);
        storage.CopyFile(source, target, overwrite: false);

        return new()
        {
            Path = relative,
            Size = storage.GetFileSize(target),
            Sha256 = await BackupFiles.Sha256Async(storage, target, cancellationToken),
        };
    }

    private async Task<BackupManifest?> ReadManifestAsync(
        string id,
        CancellationToken cancellationToken
    )
    {
        string path = Path.Combine(options.SetsRoot, id, BackupManifest.FileName);
        if (!storage.FileExists(path))
            return null;

        BackupManifest? manifest = BackupFiles.Deserialize<BackupManifest>(
            await BackupFiles.ReadTextAsync(storage, path, cancellationToken)
        );
        return manifest?.Id == id ? manifest : null;
    }

    /// <summary>Published set ids, newest first.</summary>
    private List<string> ListSetIds() =>
        storage
            .EnumerateFileSystemEntries(options.SetsRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(storage.DirectoryExists)
            .Select(path => Path.GetFileName(path))
            .Where(BackupSetId.IsValid)
            .OfType<string>()
            .OrderDescending(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Keeps the newest <see cref="BackupOptions.RetainCount"/> sets and clears any
    /// folder a crashed backup left half built. Runs under the gate, so no set is
    /// being built while it looks.
    /// </summary>
    private void Prune()
    {
        try
        {
            foreach (string id in ListSetIds().Skip(Math.Max(options.RetainCount, 1)))
            {
                storage.DeleteDirectory(Path.Combine(options.SetsRoot, id), recursive: true);
                logger.LogInformation("Pruned old backup {BackupId}", id);
            }

            List<string> leftovers = storage
                .EnumerateFileSystemEntries(
                    options.SetsRoot,
                    BackupSetId.IncompletePrefix + "*",
                    SearchOption.TopDirectoryOnly
                )
                .Where(storage.DirectoryExists)
                .ToList();
            foreach (string leftover in leftovers)
                storage.DeleteDirectory(leftover, recursive: true);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Backup pruning failed: {Reason}", exception.Message);
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (storage.DirectoryExists(path))
                storage.DeleteDirectory(path, recursive: true);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Could not remove the unfinished backup folder {Folder}: {Reason}",
                path,
                exception.Message
            );
        }
    }
}
