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
/// Applies a restore in three phases. Nothing live moves until the last check
/// has passed:
/// <list type="number">
/// <item>Prepare: verify the set, copy it into a staging folder next to the live
/// files, verify the copy again, check each database, and migrate the copies to
/// this server's schema. Any failure here refuses the restore; no live file has
/// been touched.</item>
/// <item>Swap: write a journal, then for each database file and the config folder
/// rename the live one aside and the staged one into place. Renames on one volume
/// are atomic and keep every byte of the old files.</item>
/// <item>Commit: delete the journal. A failure before this point renames
/// everything back; a crash before it is undone from the journal at the next
/// start.</item>
/// </list>
/// </summary>
public sealed class RestoreApplier(
    IStorageDriver storage,
    ISqliteFileOperations sqlite,
    IBackupSetVerifier verifier,
    IBackupDatabaseSchema schema,
    BackupOptions options,
    TimeProvider clock,
    ILogger<RestoreApplier> logger
) : IRestoreApplier
{
    private const string StagedFolder = "staged";
    private const string PreviousFolder = "previous";

    private static readonly string[] SideFileSuffixes = ["-wal", "-shm", "-journal"];

    private string JournalPath => Path.Combine(options.RestoreWorkRoot, RestoreJournal.FileName);
    private string StagedRoot => Path.Combine(options.RestoreWorkRoot, StagedFolder);
    private string PreviousRoot => Path.Combine(options.RestoreWorkRoot, PreviousFolder);

    public async Task<RestoreResult?> ApplyPendingAsync(
        bool previousServerStopped,
        CancellationToken cancellationToken
    )
    {
        RestoreResult? result = await RecoverInterruptedSwapAsync(cancellationToken);
        if (result is { Status: RestoreStatus.RollbackIncomplete })
            return result;

        PendingRestore? pending;
        try
        {
            pending = await TakePendingAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            result = Result(
                string.Empty,
                null,
                RestoreStatus.Refused,
                $"The restore request could not be read: {exception.Message}. Nothing was changed."
            );
            await SaveResultAsync(result, cancellationToken);
            return result;
        }

        if (pending is null)
            return result;

        logger.LogInformation("Restoring backup {BackupId}", pending.BackupId);
        result = await ApplyAsync(pending, previousServerStopped, cancellationToken);
        await SaveResultAsync(result, cancellationToken);
        return result;
    }

    private async Task<RestoreResult> ApplyAsync(
        PendingRestore pending,
        bool previousServerStopped,
        CancellationToken cancellationToken
    )
    {
        if (!previousServerStopped)
            return Refused(
                pending,
                "The previous server process was still running, so its database files "
                    + "could not be swapped safely. Nothing was changed; ask for the restore again."
            );

        if (!BackupSetId.IsValid(pending.BackupId))
            return Refused(pending, "The restore request does not name a valid backup.");

        string setFolder = Path.Combine(options.SetsRoot, pending.BackupId);
        if (!storage.DirectoryExists(setFolder))
            return Refused(pending, "The backup no longer exists.");

        BackupManifest manifest;
        try
        {
            BackupVerification source = await verifier.VerifyAsync(
                setFolder,
                pending.BackupId,
                cancellationToken
            );
            if (!source.IsValid)
                return Refused(pending, string.Join(" ", source.Problems));

            manifest = source.Manifest!;
            string? problem = await PrepareAsync(setFolder, manifest, cancellationToken);
            if (problem is not null)
            {
                DeleteWorkFolder();
                return Refused(pending, problem);
            }
        }
        catch (Exception exception)
        {
            DeleteWorkFolder();
            return Refused(pending, $"The backup could not be prepared: {exception.Message}");
        }

        return await SwapAsync(pending, manifest, cancellationToken);
    }

    /// <summary>
    /// Stages and checks a copy of the set. Returns why it cannot be restored, or
    /// null when the staged copy is ready to swap in.
    /// </summary>
    private async Task<string?> PrepareAsync(
        string setFolder,
        BackupManifest manifest,
        CancellationToken cancellationToken
    )
    {
        DeleteWorkFolder();
        BackupFiles.CreatePrivateDirectory(storage, options.RestoreRoot);
        BackupFiles.CreatePrivateDirectory(storage, options.RestoreWorkRoot);
        BackupFiles.CreatePrivateDirectory(storage, StagedRoot);
        storage.CreateDirectory(Path.Combine(StagedRoot, BackupSetWriter.ConfigFolder));

        // Every staged file is flushed to disk as it is copied: the checks below
        // must judge what a power cut would leave, not what sits in a cache.
        IEnumerable<BackupManifestFile> files = manifest.Databases.Concat<BackupManifestFile>(
            manifest.ConfigFiles
        );
        foreach (BackupManifestFile file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string target = BackupFiles.ResolveInside(StagedRoot, file.Path);
            storage.CreateDirectory(Path.GetDirectoryName(target)!);
            await BackupFiles.CopyDurablyAsync(
                storage,
                BackupFiles.ResolveInside(setFolder, file.Path),
                target,
                cancellationToken
            );
        }
        await BackupFiles.CopyDurablyAsync(
            storage,
            Path.Combine(setFolder, BackupManifest.FileName),
            Path.Combine(StagedRoot, BackupManifest.FileName),
            cancellationToken
        );

        // The copy is checked again, not trusted: a short read or a bad sector
        // while staging must not reach the live folder.
        BackupVerification staged = await verifier.VerifyAsync(
            StagedRoot,
            manifest.Id,
            cancellationToken
        );
        if (!staged.IsValid)
            return "The staged copy of the backup failed its check: "
                + string.Join(" ", staged.Problems);

        foreach (BackupManifestDatabase database in manifest.Databases)
        {
            string path = BackupFiles.ResolveInside(StagedRoot, database.Path);
            if (!sqlite.PassesQuickCheck(path))
                return $"The {database.Name} database in the backup is damaged.";
        }

        foreach (BackupManifestDatabase database in manifest.Databases)
        {
            string path = BackupFiles.ResolveInside(StagedRoot, database.Path);
            await schema.MigrateAsync(database.Name, path, cancellationToken);
            sqlite.Seal(path);
            if (SideFileSuffixes.Any(suffix => storage.FileExists(path + suffix)))
                return $"The staged {database.Name} database did not close cleanly.";
        }

        return null;
    }

    private async Task<RestoreResult> SwapAsync(
        PendingRestore pending,
        BackupManifest manifest,
        CancellationToken cancellationToken
    )
    {
        RestoreJournal journal = BuildJournal(pending, manifest);
        try
        {
            sqlite.ReleaseAllConnections();
            BackupFiles.CreatePrivateDirectory(storage, PreviousRoot);
            BackupFiles.CreatePrivateDirectory(
                storage,
                Path.Combine(PreviousRoot, BackupSetWriter.DatabasesFolder)
            );
            await BackupFiles.WriteTextAtomicallyAsync(
                storage,
                JournalPath,
                BackupFiles.Serialize(journal),
                cancellationToken
            );
        }
        catch (Exception exception)
        {
            DeleteWorkFolder();
            return Refused(pending, $"The restore could not start: {exception.Message}");
        }

        try
        {
            foreach (RestoreJournalStep step in journal.Steps)
                Forward(step);

            // The commit point: from here a restart keeps the restored files.
            storage.DeleteFile(JournalPath);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Putting backup {BackupId} in place failed, undoing: {Reason}",
                pending.BackupId,
                exception.Message
            );
            return Undo(journal, $"Putting the backup in place failed ({exception.Message}).");
        }

        DeleteWorkFolder();
        logger.LogInformation("Backup {BackupId} restored", pending.BackupId);
        return Result(
            pending.BackupId,
            pending.SafetyBackupId,
            RestoreStatus.Applied,
            "The backup was restored."
        );
    }

    private RestoreJournal BuildJournal(PendingRestore pending, BackupManifest manifest)
    {
        RestoreJournal journal = new()
        {
            BackupId = pending.BackupId,
            SafetyBackupId = pending.SafetyBackupId,
            StartedAt = clock.GetUtcNow().UtcDateTime,
        };
        string previousDatabases = Path.Combine(PreviousRoot, BackupSetWriter.DatabasesFolder);

        foreach (BackupDatabaseFile database in options.Databases)
        {
            // A stale -wal next to the restored file would be replayed into it,
            // so every side file of the live database moves aside with it.
            foreach (string suffix in SideFileSuffixes)
                if (storage.FileExists(database.LivePath + suffix))
                    journal.Steps.Add(
                        new()
                        {
                            Live = database.LivePath + suffix,
                            Previous = Path.Combine(previousDatabases, database.FileName + suffix),
                        }
                    );

            BackupManifestDatabase staged = manifest.Databases.Single(entry =>
                entry.Name == database.Name
            );
            journal.Steps.Add(
                new()
                {
                    Live = database.LivePath,
                    Previous = Path.Combine(previousDatabases, database.FileName),
                    Staged = BackupFiles.ResolveInside(StagedRoot, staged.Path),
                }
            );
        }

        journal.Steps.Add(
            new()
            {
                Live = options.ConfigRoot,
                Previous = Path.Combine(PreviousRoot, BackupSetWriter.ConfigFolder),
                Staged = Path.Combine(StagedRoot, BackupSetWriter.ConfigFolder),
                IsDirectory = true,
            }
        );

        return journal;
    }

    private void Forward(RestoreJournalStep step)
    {
        if (Exists(step.Live, step.IsDirectory))
            Move(step.Live, step.Previous, step.IsDirectory);
        if (step.Staged is not null)
            Move(step.Staged, step.Live, step.IsDirectory);
    }

    /// <summary>
    /// Undoes every step, newest first, from what exists on disk, so it works the
    /// same after a failed step as after a crash. A step that cannot be undone is
    /// reported and the rest are still tried.
    /// </summary>
    private List<string> UndoSteps(RestoreJournal journal)
    {
        List<string> failures = [];
        for (int index = journal.Steps.Count - 1; index >= 0; index--)
        {
            RestoreJournalStep step = journal.Steps[index];
            try
            {
                bool restoredInPlace =
                    step.Staged is not null
                    && !Exists(step.Staged, step.IsDirectory)
                    && Exists(step.Live, step.IsDirectory);
                if (restoredInPlace)
                    Move(step.Live, step.Staged!, step.IsDirectory);

                if (Exists(step.Previous, step.IsDirectory))
                {
                    if (Exists(step.Live, step.IsDirectory))
                        throw new IOException(
                            $"both '{step.Live}' and the original at '{step.Previous}' exist"
                        );
                    Move(step.Previous, step.Live, step.IsDirectory);
                }
            }
            catch (Exception exception)
            {
                failures.Add($"{step.Live}: {exception.Message}");
            }
        }

        return failures;
    }

    private RestoreResult Undo(RestoreJournal journal, string cause)
    {
        List<string> failures = UndoSteps(journal);
        if (failures.Count > 0)
        {
            logger.LogCritical(
                "Undoing the restore of {BackupId} failed; the journal is kept: {Failures}",
                journal.BackupId,
                string.Join("; ", failures)
            );
            return Result(
                journal.BackupId,
                journal.SafetyBackupId,
                RestoreStatus.RollbackIncomplete,
                $"{cause} Putting the original files back failed for: {string.Join("; ", failures)}. "
                    + $"The originals are kept in '{PreviousRoot}'. The server will not start "
                    + "until they are back; every start tries again."
            );
        }

        TryDeleteFile(JournalPath);
        DeleteWorkFolder();
        return Result(
            journal.BackupId,
            journal.SafetyBackupId,
            RestoreStatus.RolledBack,
            $"{cause} Every file was put back as it was; nothing was restored."
        );
    }

    private async Task<RestoreResult?> RecoverInterruptedSwapAsync(
        CancellationToken cancellationToken
    )
    {
        if (!storage.FileExists(JournalPath))
            return null;

        RestoreJournal? journal;
        try
        {
            journal = BackupFiles.Deserialize<RestoreJournal>(
                await BackupFiles.ReadTextAsync(storage, JournalPath, cancellationToken)
            );
        }
        catch (Exception exception)
        {
            logger.LogError("The restore journal cannot be read: {Reason}", exception.Message);
            journal = null;
        }

        RestoreResult result = journal is null
            ? Result(
                string.Empty,
                null,
                RestoreStatus.RollbackIncomplete,
                $"A restore was cut off and its journal at '{JournalPath}' cannot be read, "
                    + $"so it cannot be undone automatically. The original files are in '{PreviousRoot}'."
            )
            : Undo(journal, "The server stopped while a restore was being put in place.");

        logger.LogWarning(
            "Interrupted restore found: {Status}. {Message}",
            result.Status,
            result.Message
        );
        await SaveResultAsync(result, cancellationToken);
        return result;
    }

    /// <summary>
    /// Reads and removes the request first, so a restore that brings the server
    /// down is never tried again on its own at every start.
    /// </summary>
    private async Task<PendingRestore?> TakePendingAsync(CancellationToken cancellationToken)
    {
        if (!storage.FileExists(options.PendingRestorePath))
            return null;

        string json = await BackupFiles.ReadTextAsync(
            storage,
            options.PendingRestorePath,
            cancellationToken
        );
        storage.DeleteFile(options.PendingRestorePath);
        return BackupFiles.Deserialize<PendingRestore>(json) ?? new PendingRestore();
    }

    private async Task SaveResultAsync(RestoreResult result, CancellationToken cancellationToken)
    {
        try
        {
            storage.CreateDirectory(options.RestoreRoot);
            await BackupFiles.WriteTextAtomicallyAsync(
                storage,
                options.LastRestorePath,
                BackupFiles.Serialize(result),
                cancellationToken
            );
        }
        catch (Exception exception)
        {
            logger.LogWarning("Could not record the restore result: {Reason}", exception.Message);
        }
    }

    private RestoreResult Refused(PendingRestore pending, string message)
    {
        logger.LogWarning(
            "Restore of backup {BackupId} refused, nothing was changed: {Reason}",
            pending.BackupId,
            message
        );
        return Result(pending.BackupId, pending.SafetyBackupId, RestoreStatus.Refused, message);
    }

    private RestoreResult Result(
        string backupId,
        string? safetyBackupId,
        RestoreStatus status,
        string message
    ) =>
        new()
        {
            BackupId = backupId,
            SafetyBackupId = safetyBackupId,
            Status = status,
            Message = message,
            FinishedAt = clock.GetUtcNow().UtcDateTime,
        };

    private bool Exists(string path, bool isDirectory) =>
        isDirectory ? storage.DirectoryExists(path) : storage.FileExists(path);

    private void Move(string from, string to, bool isDirectory)
    {
        if (isDirectory)
            storage.MoveDirectory(from, to);
        else
            storage.MoveFile(from, to);
    }

    private void DeleteWorkFolder()
    {
        try
        {
            if (storage.DirectoryExists(options.RestoreWorkRoot))
                storage.DeleteDirectory(options.RestoreWorkRoot, recursive: true);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Could not clear the restore work folder {Folder}: {Reason}",
                options.RestoreWorkRoot,
                exception.Message
            );
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (storage.FileExists(path))
                storage.DeleteFile(path);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Could not delete {File}: {Reason}", path, exception.Message);
        }
    }
}
