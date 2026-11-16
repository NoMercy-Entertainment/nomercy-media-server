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
    ILogger<BackupService> logger
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
            BackupManifest manifest = await writer.WriteAsync(
                reason,
                options.Databases,
                options.ConfigRoot,
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
            BackupManifest safety = await writer.WriteAsync(
                $"before restoring {backupId}",
                options.Databases,
                options.ConfigRoot,
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
            foreach (string id in prunable.Skip(Math.Max(options.RetainCount, 1)))
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
