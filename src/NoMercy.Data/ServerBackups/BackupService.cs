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

using System.Globalization;
using Microsoft.Extensions.Logging;
using NoMercy.Storage;

namespace NoMercy.Data.ServerBackups;

/// <summary>
/// Owns every write to the sets folder and to the restore request, behind one
/// gate, so a prune can never run between a set being made and the request
/// that protects it being written.
/// </summary>
public sealed class BackupService(
    IStorageDriver storage,
    IBackupSetWriter writer,
    BackupOptions options,
    TimeProvider clock,
    ILogger<BackupService> logger,
    IVolumeInspector? volumes = null
) : IBackupService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<BackupSetSummary> CreateAsync(
        string reason,
        CancellationToken cancellationToken
    )
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            BackupManifest manifest = await WriteSetAsync(
                reason,
                keepDamagedCopies: false,
                cancellationToken
            );
            logger.LogInformation(
                "Backup {BackupId} created ({Reason})",
                manifest.Id,
                manifest.Reason
            );
            await PruneAsync(cancellationToken);
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

    public async Task<RestoreScheduleResult> RequestRestoreAsync(
        string backupId,
        CancellationToken cancellationToken
    )
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (storage.FileExists(options.PendingRestorePath))
                return new(
                    RestoreScheduleStatus.AlreadyPending,
                    ["Another restore is already waiting for the server to restart."],
                    null
                );
            if (!storage.DirectoryExists(Path.Combine(options.SetsRoot, backupId)))
                return new(RestoreScheduleStatus.NotFound, ["No backup by that id."], null);

            // The safety set comes first and the request second: a request on disk
            // always names a safety set that exists. No prune runs in between,
            // because every prune waits for this gate.
            BackupManifest safety = await WriteSetAsync(
                $"before restoring {backupId}",
                keepDamagedCopies: true,
                CancellationToken.None
            );

            PendingRestore pending = new()
            {
                BackupId = backupId,
                RequestedAt = clock.GetUtcNow().UtcDateTime,
                SafetyBackupId = safety.Id,
            };
            BackupFiles.CreatePrivateDirectory(storage, options.RestoreRoot);
            await BackupFiles.WriteTextAtomicallyAsync(
                storage,
                options.PendingRestorePath,
                BackupFiles.Serialize(pending),
                CancellationToken.None
            );

            logger.LogInformation(
                "Restore of backup {BackupId} requested; safety backup {SafetyId}",
                backupId,
                safety.Id
            );
            await PruneAsync(CancellationToken.None);
            return new(RestoreScheduleStatus.Scheduled, [], pending);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> CancelRestoreAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!storage.FileExists(options.PendingRestorePath))
                return false;

            storage.DeleteFile(options.PendingRestorePath);
            logger.LogInformation("The waiting restore was withdrawn");
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<BackupManifest?> ReadManifestAsync(
        string id,
        CancellationToken cancellationToken
    )
    {
        BackupManifest? manifest = await BackupFiles.ReadJsonAsync<BackupManifest>(
            storage,
            Path.Combine(options.SetsRoot, id, BackupManifest.FileName),
            cancellationToken
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
    /// Keeps the newest <see cref="BackupOptions.RetainCount"/> sets, plus every set
    /// a restore still needs, and clears any folder a crashed backup left half
    /// built. Runs under the gate, so no set is being built while it looks.
    /// </summary>
    private async Task PruneAsync(CancellationToken cancellationToken)
    {
        try
        {
            HashSet<string> kept = await ProtectedSetIdsAsync(cancellationToken);
            List<string> prunable = ListSetIds().Where(id => !kept.Contains(id)).ToList();

            // A set with a damaged database cannot be restored, so it must not take
            // a good set's place: damaged and good sets each keep their own newest
            // RetainCount, and a damaged one is only ever pruned in favour of another.
            HashSet<string> damaged = [];
            foreach (string id in prunable)
                if (await HasDamagedDatabaseAsync(id, cancellationToken))
                    damaged.Add(id);

            int retain = Math.Max(options.RetainCount, 1);
            IEnumerable<string> pruned = prunable
                .Where(id => !damaged.Contains(id))
                .Skip(retain)
                .Concat(prunable.Where(damaged.Contains).Skip(retain));
            foreach (string id in pruned.ToList())
            {
                storage.DeleteDirectory(Path.Combine(options.SetsRoot, id), recursive: true);
                logger.LogInformation("Pruned old backup {BackupId}", id);
            }

            ClearHalfBuiltSets();
            await PruneSupersededCopiesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Backup pruning failed: {Reason}", exception.Message);
        }
    }

    /// <summary>
    /// Writes a set, after first giving back what the backups folder holds for
    /// nothing and checking that the set fits. A disk that fills up half way
    /// through a copy takes the running server's own databases down with it.
    /// </summary>
    private async Task<BackupManifest> WriteSetAsync(
        string reason,
        bool keepDamagedCopies,
        CancellationToken cancellationToken
    )
    {
        try
        {
            if (storage.DirectoryExists(options.SetsRoot))
            {
                ClearHalfBuiltSets();
                await PruneSupersededCopiesAsync(cancellationToken);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning("Making room for a backup failed: {Reason}", exception.Message);
        }

        RequireRoomForASet();

        return await writer.WriteAsync(
            reason,
            options.Databases,
            options.ConfigRoot,
            keepDamagedCopies,
            cancellationToken
        );
    }

    /// <summary>
    /// Refuses to start a set that cannot fit, with the two numbers that say
    /// why. Goes on when the free space cannot be told.
    /// </summary>
    private void RequireRoomForASet()
    {
        if (volumes?.FreeBytesAt(options.BackupRoot) is not { } free)
            return;

        long needed = options.Databases.Sum(database =>
            SizeOrZero(database.LivePath) + SizeOrZero(database.LivePath + "-wal")
        );
        if (free >= needed)
            return;

        logger.LogWarning(
            "No backup was made: the databases take {NeededBytes} bytes and only {FreeBytes} "
                + "bytes are free at {BackupRoot}. Free up disk space; the next backup tries again.",
            needed,
            free,
            options.BackupRoot
        );
        throw new IOException(
            $"Not enough free disk space for a backup: the databases take {needed} bytes and "
                + $"{free} bytes are free at {options.BackupRoot}. Nothing was written."
        );
    }

    private long SizeOrZero(string path) =>
        storage.FileExists(path) ? storage.GetFileSize(path) : 0;

    /// <summary>Removes every folder a backup that failed or was cut short left half built.</summary>
    private void ClearHalfBuiltSets()
    {
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

    /// <summary>
    /// Removes the copies of one database at the top of the backups folder
    /// that every kept good set is newer than.
    /// <para>
    /// Such a copy is made before a migration, and the daily backup made them
    /// too before it became a set. Before a migration only the newest
    /// <see cref="BackupOptions.RetainCount"/> are kept, but that runs when a
    /// migration is pending, which can be months apart: a server updated
    /// across the change kept five copies of every database for good. A copy
    /// that is older than every set the server keeps protects nothing a set
    /// does not, so it goes. One made since the oldest kept set stays: it can
    /// be the last state from before an update.
    /// </para>
    /// </summary>
    private async Task PruneSupersededCopiesAsync(CancellationToken cancellationToken)
    {
        if (!storage.DirectoryExists(options.SetsRoot))
            return;

        List<string> goodSets = [];
        foreach (string id in ListSetIds())
        {
            if (goodSets.Count == Math.Max(options.RetainCount, 1))
                break;

            BackupManifest? manifest = await ReadManifestAsync(id, cancellationToken);
            if (manifest is not null && !manifest.Databases.Any(database => database.Damaged))
                goodSets.Add(id);
        }

        if (goodSets.Count == 0)
            return;

        string oldestKept = DatabaseCopyName.StampOfSet(goodSets[^1]);
        HashSet<string> databases = options
            .Databases.Select(database => Path.GetFileNameWithoutExtension(database.LivePath))
            .ToHashSet(StringComparer.Ordinal);

        int removed = 0;
        long bytes = 0;
        List<string> files = storage
            .EnumerateFileSystemEntries(options.BackupRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(storage.FileExists)
            .ToList();
        foreach (string file in files)
        {
            if (
                !DatabaseCopyName.TryParse(Path.GetFileName(file), out string name, out string at)
                || !databases.Contains(name)
                || string.CompareOrdinal(at, oldestKept) >= 0
            )
                continue;

            bytes += storage.GetFileSize(file);
            storage.DeleteFile(file);
            removed++;
        }

        // Copies made by hand next to the live databases are judged by the time they were written.
        foreach (string file in HandMadeCopies(databases))
        {
            string writtenAt = storage
                .GetLastWriteTimeUtc(file)
                .ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            if (string.CompareOrdinal(writtenAt, oldestKept) >= 0)
                continue;

            bytes += storage.GetFileSize(file);
            storage.DeleteFile(file);
            removed++;
        }

        if (removed > 0)
            logger.LogInformation(
                "Removed {Count} old database copies ({Bytes} bytes): every backup set "
                    + "that is kept is newer than they are",
                removed,
                bytes
            );
    }

    private List<string> HandMadeCopies(HashSet<string> databases) =>
        storage
            .EnumerateFileSystemEntries(options.DataRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(storage.FileExists)
            .Where(file =>
                DatabaseCopyName.TryParseHandMade(Path.GetFileName(file), out string name)
                && databases.Contains(name)
            )
            .ToList();

    private async Task<bool> HasDamagedDatabaseAsync(
        string id,
        CancellationToken cancellationToken
    ) =>
        (await ReadManifestAsync(id, cancellationToken))?.Databases.Any(database =>
            database.Damaged
        ) == true;

    /// <summary>
    /// The last restore's result, or null. A result that cannot be read (empty,
    /// <c>null</c>, broken JSON, an unknown status) would otherwise stop every
    /// prune for good, so it is renamed to <c>last-result.json.bad</c> and the
    /// prune goes on without the protection it could have given.
    /// </summary>
    private async Task<RestoreResult?> ReadLastResultOrSetAsideAsync(
        CancellationToken cancellationToken
    )
    {
        string path = options.LastRestorePath;
        if (!storage.FileExists(path))
            return null;

        RestoreResult? last = BackupFiles.Deserialize<RestoreResult>(
            await BackupFiles.ReadTextAsync(storage, path, cancellationToken)
        );
        if (last is not null)
            return last;

        logger.LogError(
            "The last restore result {File} cannot be read; it is set aside as {SetAside} "
                + "and the sets it named are no longer protected from pruning",
            path,
            path + ".bad"
        );
        try
        {
            storage.ReplaceFile(path, path + ".bad");
        }
        catch (Exception exception)
        {
            logger.LogError("Could not set {File} aside: {Reason}", path, exception.Message);
        }

        return null;
    }

    /// <summary>
    /// The sets no prune may delete: the target and the safety set of a waiting
    /// restore, and the safety and pre-restore sets of the last restore, which
    /// stay until a newer restore replaces that result.
    /// </summary>
    private async Task<HashSet<string>> ProtectedSetIdsAsync(CancellationToken cancellationToken)
    {
        PendingRestore? pending = await BackupFiles.ReadJsonAsync<PendingRestore>(
            storage,
            options.PendingRestorePath,
            cancellationToken
        );
        // An unreadable request protects a restore that is about to run: prune
        // nothing rather than guess which set it needs.
        if (pending is null && storage.FileExists(options.PendingRestorePath))
            throw new InvalidDataException(
                "The restore request cannot be read; nothing is pruned."
            );

        RestoreResult? last = await ReadLastResultOrSetAsideAsync(cancellationToken);

        return new[]
        {
            pending?.BackupId,
            pending?.SafetyBackupId,
            last?.SafetyBackupId,
            last?.PreRestoreBackupId,
        }
            .OfType<string>()
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }
}
