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
/// Applies a restore in four phases. Nothing live moves until the last check
/// has passed:
/// <list type="number">
/// <item>Check: the request names a set and a safety set that both exist, the
/// files to swap share one volume, and the set verifies.</item>
/// <item>Prepare: copy the set into a staging folder next to the live files,
/// flushed to disk, verify the copy again, check each database, and migrate the
/// copies to this server's schema. Any failure in these two phases refuses the
/// restore; no live file has been touched.</item>
/// <item>Swap: write a journal, then for each database file and the config folder
/// rename the live one aside and the staged one into place. Renames on one volume
/// are atomic and keep every byte of the old files.</item>
/// <item>Commit: delete the journal. A failure before this point renames
/// everything back; a crash before it is undone from the journal at the next
/// start. After it, the files that were renamed aside are published as a
/// backup set, the true state from before the restore.</item>
/// </list>
/// </summary>
public sealed class RestoreApplier(
    IStorageDriver storage,
    ISqliteFileOperations sqlite,
    IBackupSetVerifier verifier,
    IBackupDatabaseSchema schema,
    IBackupSetWriter writer,
    IVolumeInspector volumes,
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
        bool noOtherServerRunning,
        CancellationToken cancellationToken
    )
    {
        // Another server may be in the middle of its own swap: then neither its
        // journal nor its request is ours to touch, and nothing is recorded.
        if (!noOtherServerRunning)
            return await RefuseWhileAnotherServerRunsAsync(cancellationToken);

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

        // Recorded first, so a crash while preparing still leaves a word on it.
        await SaveResultAsync(
            Result(
                pending.BackupId,
                pending.SafetyBackupId,
                RestoreStatus.Refused,
                "The server stopped while preparing this restore. Nothing was changed."
            ),
            cancellationToken
        );

        result = await ApplyAsync(pending, cancellationToken);
        await SaveResultAsync(result, cancellationToken);
        return result;
    }

    private async Task<RestoreResult?> RefuseWhileAnotherServerRunsAsync(
        CancellationToken cancellationToken
    )
    {
        if (!storage.FileExists(options.PendingRestorePath))
            return null;

        PendingRestore? waiting = null;
        try
        {
            waiting = await BackupFiles.ReadJsonAsync<PendingRestore>(
                storage,
                options.PendingRestorePath,
                cancellationToken
            );
        }
        catch (Exception exception)
        {
            logger.LogWarning("The restore request cannot be read: {Reason}", exception.Message);
        }

        return Refused(
            waiting ?? new PendingRestore(),
            "Another server process still holds the data folder, so its files could not be "
                + "swapped safely. Nothing was changed. The restore stays requested and runs at "
                + "the next start that has the data folder to itself; withdraw it to stop that."
        );
    }

    private async Task<RestoreResult> ApplyAsync(
        PendingRestore pending,
        CancellationToken cancellationToken
    )
    {
        if (!BackupSetId.IsValid(pending.BackupId))
            return Refused(pending, "The restore request does not name a valid backup.");

        string setFolder = Path.Combine(options.SetsRoot, pending.BackupId);
        if (!storage.DirectoryExists(setFolder))
            return Refused(pending, "The backup no longer exists.");

        if (!SafetySetExists(pending))
            return Refused(
                pending,
                "The backup of the state before this restore is missing, so the restore could "
                    + "not be undone. Nothing was changed; ask for the restore again."
            );

        if (HasFiles(PreviousRoot))
            return Refused(
                pending,
                $"The files from before an earlier restore are still in '{PreviousRoot}' and "
                    + "were never saved as a backup. Move them somewhere safe, then ask again."
            );

        BackupManifest manifest;
        try
        {
            string? layoutProblem = CheckLayout();
            if (layoutProblem is not null)
                return Refused(pending, layoutProblem);

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
                ClearWorkFolder();
                return Refused(pending, problem);
            }
        }
        catch (Exception exception)
        {
            ClearWorkFolder();
            return Refused(pending, $"The backup could not be prepared: {exception.Message}");
        }

        return await SwapAsync(pending, manifest, cancellationToken);
    }

    private bool SafetySetExists(PendingRestore pending) =>
        BackupSetId.IsValid(pending.SafetyBackupId)
        && storage.DirectoryExists(Path.Combine(options.SetsRoot, pending.SafetyBackupId!));

    /// <summary>
    /// A swap renames the databases and the config folder into the work folder
    /// and back. A rename across volumes is a copy and a delete, neither atomic
    /// nor safe to undo, so every one of them must share the work folder's volume.
    /// Returns why not, or null when they do (or when the volumes are unknown).
    /// </summary>
    private string? CheckLayout()
    {
        if (storage.ResolveLinkTarget(options.ConfigRoot) is not null)
            return $"The config folder '{options.ConfigRoot}' is a link. A restore renames "
                + "the folder itself and cannot do that safely through a link.";

        if (volumes.IsMountPoint(options.ConfigRoot))
            return $"The config folder '{options.ConfigRoot}' is a mount point. A restore "
                + "renames the folder itself, which a mount point does not allow.";

        string? workVolume = volumes.VolumeOf(options.RestoreWorkRoot);
        if (workVolume is null)
            return null;

        List<(string Label, string Path)> swapped =
        [
            ("The config folder", options.ConfigRoot),
            .. options.Databases.Select(database =>
                ($"The {database.Name} database", Path.GetDirectoryName(database.LivePath)!)
            ),
        ];
        foreach ((string label, string path) in swapped)
        {
            string? volume = volumes.VolumeOf(path);
            if (volume is not null && !volume.Equals(workVolume, StringComparison.Ordinal))
                return $"{label} is on {volume}, but the restore works in "
                    + $"'{options.RestoreWorkRoot}' on {workVolume}. A restore swaps files by "
                    + "renaming them, which only works within one volume.";
        }

        return null;
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
        ClearWorkFolder();
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
            if (sqlite.FindDamage(path) is not null)
                return $"The {database.Name} database in the backup is damaged.";
        }

        // SQLite syncs its own writes (rollback journal, synchronous=FULL), so the
        // migrated and sealed copies are on disk when these calls return.
        foreach (BackupManifestDatabase database in manifest.Databases)
        {
            string path = BackupFiles.ResolveInside(StagedRoot, database.Path);
            await schema.MigrateAsync(database.Name, path, cancellationToken);
            sqlite.Seal(path);
            if (SideFileSuffixes.Any(suffix => storage.FileExists(path + suffix)))
                return $"The staged {database.Name} database did not close cleanly.";
        }

        BackupFiles.RestrictTree(storage, StagedRoot);
        return null;
    }

    private async Task<RestoreResult> SwapAsync(
        PendingRestore pending,
        BackupManifest manifest,
        CancellationToken cancellationToken
    )
    {
        RestoreJournal journal;
        try
        {
            journal = BuildJournal(pending, manifest);
            sqlite.ReleaseAllConnections();
            BackupFiles.CreatePrivateDirectory(storage, PreviousRoot);
            BackupFiles.CreatePrivateDirectory(
                storage,
                Path.Combine(PreviousRoot, BackupSetWriter.DatabasesFolder)
            );

            // Flushed to disk before the first rename: a crash after any rename
            // must find the journal to undo it.
            await BackupFiles.WriteTextAtomicallyAsync(
                storage,
                JournalPath,
                BackupFiles.Serialize(journal),
                cancellationToken
            );
        }
        catch (Exception exception)
        {
            ClearWorkFolder();
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

        logger.LogInformation("Backup {BackupId} restored", pending.BackupId);
        (string? preRestoreId, string? keepProblem) = await KeepPreviousStateAsync(pending);
        string message = keepProblem is null
            ? $"The backup was restored. The state from just before it is kept as backup {preRestoreId}."
            : $"The backup was restored. The state from just before it could not be saved as "
                + $"a backup ({keepProblem}); its files are kept in '{PreviousRoot}'.";

        RestoreResult result = Result(
            pending.BackupId,
            pending.SafetyBackupId,
            RestoreStatus.Applied,
            message
        );
        result.PreRestoreBackupId = preRestoreId;
        return result;
    }

    /// <summary>
    /// Publishes the files the swap renamed aside as a backup set: the state the
    /// old server left when it exited, later than the safety set taken when the
    /// restore was asked for. On failure the files stay where they are.
    /// </summary>
    private async Task<(string? Id, string? Problem)> KeepPreviousStateAsync(PendingRestore pending)
    {
        try
        {
            List<BackupDatabaseFile> databases = options
                .Databases.Select(database => new BackupDatabaseFile(
                    database.Name,
                    Path.Combine(PreviousRoot, BackupSetWriter.DatabasesFolder, database.FileName)
                ))
                .ToList();
            BackupManifest kept = await writer.WriteAsync(
                $"before restore of {pending.BackupId}",
                databases,
                Path.Combine(PreviousRoot, BackupSetWriter.ConfigFolder),
                keepDamagedCopies: true,
                CancellationToken.None
            );

            sqlite.ReleaseAllConnections();
            DeleteWorkFolder();
            return (kept.Id, null);
        }
        catch (Exception exception)
        {
            logger.LogCritical(
                "The state from before the restore of {BackupId} could not be saved as a backup; "
                    + "its files are kept in {Folder}: {Reason}",
                pending.BackupId,
                PreviousRoot,
                exception.Message
            );
            sqlite.ReleaseAllConnections();
            ClearWorkFolder();
            return (null, exception.Message);
        }
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
        ClearWorkFolder();
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
            BackupFiles.CreatePrivateDirectory(storage, options.RestoreRoot);
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

    private bool HasFiles(string folder) =>
        storage.DirectoryExists(folder)
        && storage
            .EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
            .Any(storage.FileExists);

    /// <summary>
    /// Removes what a restore staged, but never a file from before a restore:
    /// while <c>previous</c> holds any, only the staged copies and the journal go.
    /// </summary>
    private void ClearWorkFolder()
    {
        if (!HasFiles(PreviousRoot))
        {
            DeleteWorkFolder();
            return;
        }

        try
        {
            if (storage.DirectoryExists(StagedRoot))
                storage.DeleteDirectory(StagedRoot, recursive: true);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Could not clear the staged restore copy {Folder}: {Reason}",
                StagedRoot,
                exception.Message
            );
        }

        TryDeleteFile(JournalPath);
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
