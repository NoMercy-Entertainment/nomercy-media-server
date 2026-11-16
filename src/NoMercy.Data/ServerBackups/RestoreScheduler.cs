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

using NoMercy.Storage;

namespace NoMercy.Data.ServerBackups;

public sealed class RestoreScheduler(
    IStorageDriver storage,
    IBackupSetVerifier verifier,
    IBackupService backups,
    BackupOptions options
) : IRestoreScheduler
{
    public async Task<RestoreScheduleResult> ScheduleAsync(
        string backupId,
        CancellationToken cancellationToken
    )
    {
        if (!BackupSetId.IsValid(backupId))
            return new(RestoreScheduleStatus.InvalidId, ["That is not a backup id."], null);

        // Cheap answers first; the backup service checks both again under its gate.
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

        return await backups.RequestRestoreAsync(backupId, cancellationToken);
    }

    public Task<bool> CancelAsync(CancellationToken cancellationToken) =>
        backups.CancelRestoreAsync(cancellationToken);

    public Task<PendingRestore?> GetPendingAsync(CancellationToken cancellationToken) =>
        BackupFiles.ReadJsonAsync<PendingRestore>(
            storage,
            options.PendingRestorePath,
            cancellationToken
        );

    public Task<RestoreResult?> GetLastResultAsync(CancellationToken cancellationToken) =>
        BackupFiles.ReadJsonAsync<RestoreResult>(
            storage,
            options.LastRestorePath,
            cancellationToken
        );
}
