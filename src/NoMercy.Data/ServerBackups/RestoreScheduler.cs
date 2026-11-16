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

public sealed class RestoreScheduler(
    IStorageDriver storage,
    IBackupSetVerifier verifier,
    IBackupService backups,
    BackupOptions options,
    TimeProvider clock,
    ILogger<RestoreScheduler> logger
) : IRestoreScheduler
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<RestoreScheduleResult> ScheduleAsync(
        string backupId,
        CancellationToken cancellationToken
    )
    {
        if (!BackupSetId.IsValid(backupId))
            return new(RestoreScheduleStatus.InvalidId, ["That is not a backup id."], null);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (storage.FileExists(options.PendingRestorePath))
                return new(
                    RestoreScheduleStatus.AlreadyPending,
                    ["Another restore is already waiting for the server to restart."],
                    null
                );

            string setFolder = Path.Combine(options.SetsRoot, backupId);
            if (!storage.DirectoryExists(setFolder))
                return new(RestoreScheduleStatus.NotFound, ["No backup by that id."], null);

            BackupVerification verification = await verifier.VerifyAsync(
                setFolder,
                backupId,
                cancellationToken
            );
            if (!verification.IsValid)
                return new(RestoreScheduleStatus.Invalid, verification.Problems, null);

            PendingRestore pending = new()
            {
                BackupId = backupId,
                RequestedAt = clock.GetUtcNow().UtcDateTime,
            };

            // The request is written before the safety backup, so the prune that
            // backup runs keeps the set this restore needs.
            await WritePendingAsync(pending, cancellationToken);
            try
            {
                BackupSetSummary safety = await backups.CreateAsync(
                    $"before restoring {backupId}",
                    CancellationToken.None
                );
                pending.SafetyBackupId = safety.Id;
                await WritePendingAsync(pending, CancellationToken.None);
            }
            catch
            {
                DeletePending();
                throw;
            }

            logger.LogInformation(
                "Restore of backup {BackupId} scheduled for the next start; safety backup {SafetyId}",
                backupId,
                pending.SafetyBackupId
            );
            return new(RestoreScheduleStatus.Scheduled, [], pending);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            DeletePending();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PendingRestore?> GetPendingAsync(CancellationToken cancellationToken) =>
        await ReadAsync<PendingRestore>(options.PendingRestorePath, cancellationToken);

    public async Task<RestoreResult?> GetLastResultAsync(CancellationToken cancellationToken) =>
        await ReadAsync<RestoreResult>(options.LastRestorePath, cancellationToken);

    private async Task WritePendingAsync(
        PendingRestore pending,
        CancellationToken cancellationToken
    )
    {
        storage.CreateDirectory(options.RestoreRoot);
        await BackupFiles.WriteTextAtomicallyAsync(
            storage,
            options.PendingRestorePath,
            BackupFiles.Serialize(pending),
            cancellationToken
        );
    }

    private void DeletePending()
    {
        if (storage.FileExists(options.PendingRestorePath))
            storage.DeleteFile(options.PendingRestorePath);
    }

    private async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken)
        where T : class =>
        storage.FileExists(path)
            ? BackupFiles.Deserialize<T>(
                await BackupFiles.ReadTextAsync(storage, path, cancellationToken)
            )
            : null;
}
