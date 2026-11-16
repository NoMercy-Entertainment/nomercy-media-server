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
    IBackupSetWriter writer,
    BackupOptions options,
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
                cancellationToken
            );
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
            // The set a waiting restore needs is never pruned, even when it is old.
            string? protectedId = PendingRestoreTarget();
            List<string> prunable = ListSetIds().Where(id => id != protectedId).ToList();
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

    private string? PendingRestoreTarget()
    {
        if (!storage.FileExists(options.PendingRestorePath))
            return null;

        using Stream stream = storage.OpenRead(options.PendingRestorePath);
        using StreamReader reader = new(stream);
        return BackupFiles.Deserialize<PendingRestore>(reader.ReadToEnd())?.BackupId;
    }
}
