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

public sealed class BackupSetWriter(
    IStorageDriver storage,
    ISqliteFileOperations sqlite,
    BackupOptions options,
    TimeProvider clock,
    ILogger<BackupSetWriter> logger
) : IBackupSetWriter
{
    public const string DatabasesFolder = "databases";
    public const string ConfigFolder = "config";

    public async Task<BackupManifest> WriteAsync(
        string reason,
        IReadOnlyList<BackupDatabaseFile> databases,
        string configRoot,
        bool keepDamagedCopies,
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

        // Backups hold app.db and the settings (API keys, signing keys), so every
        // folder on the way is limited to the owner before anything is written.
        BackupFiles.CreatePrivateDirectory(storage, options.BackupRoot);
        BackupFiles.CreatePrivateDirectory(storage, options.SetsRoot);
        BackupFiles.CreatePrivateDirectory(storage, building);
        try
        {
            BackupFiles.CreatePrivateDirectory(storage, Path.Combine(building, DatabasesFolder));
            foreach (BackupDatabaseFile database in databases)
                manifest.Databases.Add(
                    await CopyDatabaseAsync(
                        database,
                        building,
                        keepDamagedCopies,
                        cancellationToken
                    )
                );

            foreach (string relative in BackupFiles.ListFilesRelative(storage, configRoot))
                manifest.ConfigFiles.Add(
                    await CopyConfigFileAsync(configRoot, relative, building, cancellationToken)
                );

            await BackupFiles.WriteTextAtomicallyAsync(
                storage,
                Path.Combine(building, BackupManifest.FileName),
                BackupFiles.Serialize(manifest),
                cancellationToken
            );

            BackupFiles.RestrictTree(storage, building);
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
        bool keepDamagedCopies,
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
        string? damage = null;
        try
        {
            sqlite.CopyOnline(database.LivePath, target);
        }
        catch (Exception exception) when (keepDamagedCopies)
        {
            // The online copy needs a database SQLite can read. When even that
            // fails, the file as it lies on disk is all there is to keep.
            if (storage.FileExists(target))
                storage.DeleteFile(target);
            await BackupFiles.CopyDurablyAsync(
                storage,
                database.LivePath,
                target,
                cancellationToken
            );
            damage =
                $"SQLite could not copy it ({exception.Message}); the file was kept as it was "
                + "on disk, without its -wal file.";
        }

        damage ??= sqlite.FindDamage(target);
        if (damage is not null)
        {
            if (!keepDamagedCopies)
            {
                // A damaged copy must never become an ordinary set: the prune
                // would then rotate good sets out in favour of it.
                logger.LogCritical(
                    "The {Database} database fails its integrity check ({Damage}); no backup "
                        + "was made. The live database may be damaged: keep the existing backups.",
                    database.Name,
                    damage
                );
                throw new InvalidDataException(
                    $"The copy of the {database.Name} database fails its integrity check: {damage}"
                );
            }

            logger.LogError(
                "The {Database} database fails its integrity check ({Damage}); it is kept in "
                    + "this backup anyway, marked damaged, because this backup preserves the "
                    + "current state",
                database.Name,
                damage
            );
        }

        IReadOnlyList<string> applied = ReadAppliedMigrations(target, damage is not null);
        return new()
        {
            Name = database.Name,
            Path = relative,
            Size = storage.GetFileSize(target),
            Sha256 = await BackupFiles.Sha256Async(storage, target, cancellationToken),
            LastMigration = applied.Count > 0 ? applied[^1] : null,
            Damaged = damage is not null,
            Damage = damage,
        };
    }

    private IReadOnlyList<string> ReadAppliedMigrations(string path, bool damaged)
    {
        if (!damaged)
            return sqlite.ReadAppliedMigrations(path);

        try
        {
            return sqlite.ReadAppliedMigrations(path);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private async Task<BackupManifestFile> CopyConfigFileAsync(
        string configRoot,
        string relativeToConfig,
        string building,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        string relative = $"{ConfigFolder}/{relativeToConfig}";
        string source = BackupFiles.ResolveInside(configRoot, relativeToConfig);
        string target = BackupFiles.ResolveInside(building, relative);

        storage.CreateDirectory(Path.GetDirectoryName(target)!);
        await BackupFiles.CopyDurablyAsync(storage, source, target, cancellationToken);

        return new()
        {
            Path = relative,
            Size = storage.GetFileSize(target),
            Sha256 = await BackupFiles.Sha256Async(storage, target, cancellationToken),
        };
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
