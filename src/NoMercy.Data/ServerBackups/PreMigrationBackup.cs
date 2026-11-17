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

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NoMercy.Storage;

namespace NoMercy.Data.ServerBackups;

public sealed class PreMigrationBackup(
    IStorageDriver storage,
    ISqliteFileOperations sqlite,
    BackupOptions options,
    TimeProvider clock,
    ILogger<PreMigrationBackup> logger
) : IPreMigrationBackup
{
    private const string TimestampFormat = "yyyyMMddHHmmss";
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(10);

    public bool BackupBeforeMigration(string databasePath, int pendingMigrationCount)
    {
        if (pendingMigrationCount <= 0 || !storage.FileExists(databasePath))
            return false;

        string databaseName = Path.GetFileNameWithoutExtension(databasePath);
        try
        {
            BackupFiles.CreatePrivateDirectory(storage, options.BackupRoot);

            string timestamp = clock.GetUtcNow().UtcDateTime.ToString(TimestampFormat);
            string backupFileName = $"{databaseName}.{timestamp}.db";
            string backupPath = Path.Combine(options.BackupRoot, backupFileName);
            if (storage.FileExists(backupPath))
                throw new IOException($"Backup file already exists: {backupPath}");

            CopyWithProgress(databasePath, backupPath, databaseName);
            storage.TryRestrictToOwner(backupPath);
            logger.LogInformation(
                "Database backup created: {BackupFile} ({PendingCount} pending migration(s))",
                backupFileName,
                pendingMigrationCount
            );
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "WARNING: Could not back up database '{Database}': {Reason}. "
                    + "If this coincides with an upgrade, restore from a manual backup.",
                Path.GetFileName(databasePath),
                exception.Message
            );
            return false;
        }

        Prune(databaseName);
        return true;
    }

    private void CopyWithProgress(string databasePath, string backupPath, string databaseName)
    {
        long total = SizeOrZero(databasePath) + SizeOrZero(databasePath + "-wal");
        LogProgress(databaseName, 0, total);

        // The online backup call reports nothing, so the growing backup file is the signal.
        using ITimer timer = clock.CreateTimer(
            _ => LogProgress(databaseName, SizeOrZero(backupPath), total),
            null,
            ProgressInterval,
            ProgressInterval
        );
        sqlite.CopyOnline(databasePath, backupPath);
    }

    private void LogProgress(string databaseName, long done, long total)
    {
        try
        {
            long percent = total > 0 ? Math.Clamp(done * 100 / total, 0, 99) : 0;
            logger.LogInformation(
                "Backing up {Database}: {Done} of {Total} ({Percent}%)",
                databaseName,
                done,
                total,
                percent
            );
        }
        catch (Exception)
        {
            // A progress line never fails the backup.
        }
    }

    private long SizeOrZero(string path)
    {
        try
        {
            return storage.FileExists(path) ? storage.GetFileSize(path) : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Keeps the newest <see cref="BackupOptions.RetainCount"/> copies of one
    /// database. A pruned copy takes its <c>-wal</c>, <c>-shm</c> and
    /// <c>-journal</c> files with it, and a side file whose copy is already gone
    /// is removed too, so nothing of a pruned backup stays behind.
    /// </summary>
    private void Prune(string databaseName)
    {
        Regex copyPattern = new(
            $"^{Regex.Escape(databaseName)}\\.(\\d{{14}})\\.db$",
            RegexOptions.None,
            TimeSpan.FromSeconds(1)
        );
        Regex sidePattern = new(
            $"^({Regex.Escape(databaseName)}\\.\\d{{14}}\\.db)-(wal|shm|journal)$",
            RegexOptions.None,
            TimeSpan.FromSeconds(1)
        );

        try
        {
            List<string> entries = storage
                .EnumerateFileSystemEntries(options.BackupRoot, "*", SearchOption.TopDirectoryOnly)
                .Where(storage.FileExists)
                .ToList();

            List<string> copies = entries
                .Where(entry => copyPattern.IsMatch(Path.GetFileName(entry)))
                .OrderBy(entry => Path.GetFileName(entry), StringComparer.Ordinal)
                .ToList();

            HashSet<string> kept = copies
                .Skip(Math.Max(copies.Count - options.RetainCount, 0))
                .Select(path => Path.GetFileName(path))
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);

            foreach (string copy in copies.Where(copy => !kept.Contains(Path.GetFileName(copy))))
            {
                storage.DeleteFile(copy);
                logger.LogDebug("Pruned old backup: {BackupFile}", Path.GetFileName(copy));
            }

            foreach (string entry in entries)
            {
                Match side = sidePattern.Match(Path.GetFileName(entry));
                if (side.Success && !kept.Contains(side.Groups[1].Value))
                    storage.DeleteFile(entry);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "WARNING: Backup pruning failed for '{Database}': {Reason}",
                databaseName,
                exception.Message
            );
        }
    }
}
