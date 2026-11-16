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

public sealed class BackupSetVerifier(
    IStorageDriver storage,
    ISqliteFileOperations sqlite,
    IBackupDatabaseSchema schema,
    BackupOptions options
) : IBackupSetVerifier
{
    public async Task<BackupVerification> VerifyAsync(
        string setFolder,
        string expectedId,
        CancellationToken cancellationToken
    )
    {
        string manifestPath = Path.Combine(setFolder, BackupManifest.FileName);
        if (!storage.FileExists(manifestPath))
            return new(null, ["The backup has no manifest."]);

        BackupManifest? manifest = BackupFiles.Deserialize<BackupManifest>(
            await BackupFiles.ReadTextAsync(storage, manifestPath, cancellationToken)
        );
        if (manifest is null)
            return new(null, ["The backup manifest cannot be read."]);

        List<string> problems = [];
        if (manifest.FormatVersion > BackupManifest.CurrentFormatVersion)
            problems.Add(
                $"The backup was written by a newer server (manifest format {manifest.FormatVersion})."
            );
        if (manifest.Id != expectedId)
            problems.Add($"The manifest names backup '{manifest.Id}', not '{expectedId}'.");

        CheckDatabaseList(manifest, problems);

        foreach (
            BackupManifestFile file in manifest.Databases.Concat<BackupManifestFile>(
                manifest.ConfigFiles
            )
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CheckFileAsync(setFolder, file, problems, cancellationToken);
        }

        if (problems.Count == 0)
            foreach (BackupManifestDatabase database in manifest.Databases)
                CheckSchema(setFolder, database, problems);

        return new(manifest, problems);
    }

    private void CheckDatabaseList(BackupManifest manifest, List<string> problems)
    {
        List<string> expected = options
            .Databases.Select(database => database.Name)
            .Order()
            .ToList();
        List<string> present = manifest
            .Databases.Select(database => database.Name)
            .Order()
            .ToList();
        if (!expected.SequenceEqual(present))
            problems.Add(
                $"The backup holds the databases [{string.Join(", ", present)}], "
                    + $"but this server runs [{string.Join(", ", expected)}]."
            );

        foreach (BackupManifestDatabase database in manifest.Databases)
            if (
                !database.Path.StartsWith(
                    BackupService.DatabasesFolder + "/",
                    StringComparison.Ordinal
                )
            )
                problems.Add($"The {database.Name} database is outside the databases folder.");

        foreach (BackupManifestFile file in manifest.ConfigFiles)
            if (!file.Path.StartsWith(BackupService.ConfigFolder + "/", StringComparison.Ordinal))
                problems.Add($"The settings file '{file.Path}' is outside the config folder.");
    }

    private async Task CheckFileAsync(
        string setFolder,
        BackupManifestFile file,
        List<string> problems,
        CancellationToken cancellationToken
    )
    {
        string path;
        try
        {
            path = BackupFiles.ResolveInside(setFolder, file.Path);
        }
        catch (InvalidDataException exception)
        {
            problems.Add(exception.Message);
            return;
        }

        if (!storage.FileExists(path))
        {
            problems.Add($"'{file.Path}' is missing.");
            return;
        }

        if (storage.GetFileSize(path) != file.Size)
        {
            problems.Add($"'{file.Path}' has the wrong size.");
            return;
        }

        string checksum = await BackupFiles.Sha256Async(storage, path, cancellationToken);
        if (!checksum.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            problems.Add($"'{file.Path}' does not match its checksum.");
    }

    private void CheckSchema(
        string setFolder,
        BackupManifestDatabase database,
        List<string> problems
    )
    {
        IReadOnlyList<string> applied;
        try
        {
            applied = sqlite.ReadAppliedMigrations(
                BackupFiles.ResolveInside(setFolder, database.Path)
            );
        }
        catch (Exception exception)
        {
            problems.Add($"The {database.Name} database cannot be opened: {exception.Message}");
            return;
        }

        HashSet<string> known = schema
            .KnownMigrations(database.Name)
            .ToHashSet(StringComparer.Ordinal);
        List<string> unknown = applied.Where(migration => !known.Contains(migration)).ToList();
        if (unknown.Count > 0)
            problems.Add(
                $"The {database.Name} database is at migration {unknown[^1]}, which this server "
                    + "does not know: the backup comes from a newer server version. "
                    + "Update the server first, then restore."
            );
    }
}
