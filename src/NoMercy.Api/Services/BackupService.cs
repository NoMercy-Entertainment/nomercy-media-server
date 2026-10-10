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

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.NmSystem.Information;
using NoMercy.Storage;

namespace NoMercy.Api.Services;

public sealed class BackupService(IStorageDriver storage) : IBackupService
{
    private static readonly SemaphoreSlim OperationLock = new(1, 1);
    private readonly IStorageDriver _storage = storage;

    public string BackupRoot { get; set; } = Path.Combine(AppFiles.DataPath, "backups", "complete");
    public string ConfigRoot { get; set; } = AppFiles.ConfigPath;
    public string[] DatabasePaths { get; set; } =
    [AppFiles.MediaDatabase, AppFiles.QueueDatabase, AppFiles.AppDatabase];
    public int RetainCount { get; set; } = 5;

    public async Task<string> CreateAsync(CancellationToken cancellationToken = default)
    {
        await OperationLock.WaitAsync(cancellationToken);
        try
        {
            if (RetainCount < 1)
                throw new InvalidOperationException("Backup retention must be at least one.");
            foreach (string databasePath in DatabasePaths)
                if (!_storage.FileExists(databasePath))
                    throw new FileNotFoundException(
                        "A required backup database is missing.",
                        databasePath
                    );

            string id = $"{DateTime.UtcNow:yyyyMMddHHmmssfffffff}-{Guid.NewGuid():N}";
            string folder = Path.Combine(BackupRoot, id);
            _storage.CreateDirectory(folder);
            try
            {
                foreach (string databasePath in DatabasePaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string name = Path.GetFileName(databasePath);
                    string temporary = Path.Combine(folder, $".{name}.staging.db");
                    using (SqliteConnection source = OpenSqlite(databasePath))
                    using (SqliteConnection destination = OpenSqlite(temporary))
                        source.BackupDatabase(destination);
                    _storage.MoveFile(temporary, Path.Combine(folder, name));
                }

                List<string> configFiles = [];
                if (_storage.DirectoryExists(ConfigRoot))
                {
                    foreach (
                        string file in _storage.EnumerateFileSystemEntries(
                            ConfigRoot,
                            "*",
                            SearchOption.AllDirectories
                        )
                    )
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (_storage.ResolveLinkTarget(file) is not null)
                            throw new InvalidOperationException(
                                "Config symlinks cannot be backed up."
                            );
                        if (_storage.DirectoryExists(file))
                            continue;
                        string relative = Path.GetRelativePath(ConfigRoot, file);
                        string target = Path.Combine(folder, "config", relative);
                        _storage.CreateDirectory(Path.GetDirectoryName(target)!);
                        _storage.CopyFile(file, target, false);
                        configFiles.Add(relative.Replace('\\', '/'));
                    }
                }

                BackupManifest manifest = new(
                    DatabasePaths.Select(Path.GetFileName).Select(name => name!).ToArray(),
                    configFiles.Order(StringComparer.Ordinal).ToArray()
                );
                using (
                    Stream stream = _storage.OpenWrite(Path.Combine(folder, "manifest.json"), false)
                )
                    await JsonSerializer.SerializeAsync(
                        stream,
                        manifest,
                        cancellationToken: cancellationToken
                    );
                await PruneAsync(cancellationToken);
                return id;
            }
            catch
            {
                _storage.DeleteDirectory(folder, true);
                throw;
            }
        }
        finally
        {
            OperationLock.Release();
        }
    }

    public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_storage.DirectoryExists(BackupRoot))
            return Task.FromResult<IReadOnlyList<string>>([]);

        IReadOnlyList<string> ids = _storage
            .EnumerateFileSystemEntries(BackupRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(path =>
                _storage.DirectoryExists(path)
                && _storage.FileExists(Path.Combine(path, "manifest.json"))
            )
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!)
            .OrderDescending(StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult(ids);
    }

    public async Task RestoreAsync(string backupId, CancellationToken cancellationToken = default)
    {
        if (
            string.IsNullOrWhiteSpace(backupId)
            || backupId.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')
        )
            throw new ArgumentException("Invalid backup id.", nameof(backupId));

        await OperationLock.WaitAsync(cancellationToken);
        string staging = Path.Combine(BackupRoot, $".restore-{Guid.NewGuid():N}");
        try
        {
            string folder = Path.Combine(BackupRoot, backupId);
            string manifestPath = Path.Combine(folder, "manifest.json");
            if (!_storage.FileExists(manifestPath))
                throw new FileNotFoundException("Backup manifest was not found.", manifestPath);
            BackupManifest manifest;
            using (Stream stream = _storage.OpenRead(manifestPath))
                manifest =
                    await JsonSerializer.DeserializeAsync<BackupManifest>(
                        stream,
                        cancellationToken: cancellationToken
                    ) ?? throw new InvalidDataException("Backup manifest is empty.");

            string[] expected = DatabasePaths
                .Select(Path.GetFileName)
                .Select(name => name!)
                .ToArray();
            if (!manifest.Databases.Order().SequenceEqual(expected.Order()))
                throw new InvalidDataException("Backup does not contain the required databases.");

            _storage.CreateDirectory(staging);
            foreach (string databasePath in DatabasePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = Path.GetFileName(databasePath);
                _storage.CopyFile(Path.Combine(folder, name), Path.Combine(staging, name), false);
                await ValidateAndMigrateAsync(Path.Combine(staging, name), name, cancellationToken);
            }
            foreach (string relative in manifest.ConfigFiles)
            {
                string source = SafeConfigPath(Path.Combine(folder, "config"), relative);
                SafeConfigPath(ConfigRoot, relative);
                if (!_storage.FileExists(source))
                    throw new InvalidDataException($"Backup config file is missing: {relative}");
            }

            foreach (string databasePath in DatabasePaths)
            {
                string name = Path.GetFileName(databasePath);
                using SqliteConnection source = OpenSqlite(Path.Combine(staging, name));
                using SqliteConnection destination = OpenSqlite(databasePath);
                source.BackupDatabase(destination);
            }
            if (_storage.DirectoryExists(ConfigRoot))
            {
                string[] currentFiles = _storage
                    .EnumerateFileSystemEntries(ConfigRoot, "*", SearchOption.AllDirectories)
                    .Where(path => !_storage.DirectoryExists(path))
                    .ToArray();
                foreach (string file in currentFiles)
                    _storage.DeleteFile(file);
            }
            foreach (string relative in manifest.ConfigFiles)
            {
                string target = SafeConfigPath(ConfigRoot, relative);
                _storage.CreateDirectory(Path.GetDirectoryName(target)!);
                _storage.CopyFile(
                    SafeConfigPath(Path.Combine(folder, "config"), relative),
                    target,
                    true
                );
            }
            SqliteConnection.ClearAllPools();
        }
        finally
        {
            if (_storage.DirectoryExists(staging))
                _storage.DeleteDirectory(staging, true);
            OperationLock.Release();
        }
    }

    private async Task PruneAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> backups = await ListAsync(cancellationToken);
        foreach (string id in backups.Skip(RetainCount))
            _storage.DeleteDirectory(Path.Combine(BackupRoot, id), true);
    }

    private static SqliteConnection OpenSqlite(string path)
    {
        SqliteConnection connection = new(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()
        );
        connection.Open();
        return connection;
    }

    private static string SafeConfigPath(string root, string relative)
    {
        string fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(Path.Combine(root, relative));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!fullPath.StartsWith(fullRoot, comparison))
            throw new InvalidDataException("Backup contains a config path outside its root.");
        return fullPath;
    }

    private static async Task ValidateAndMigrateAsync(
        string path,
        string name,
        CancellationToken cancellationToken
    )
    {
        await using DbContext context = name switch
        {
            "media.db" => new MediaContext(
                new DbContextOptionsBuilder<MediaContext>()
                    .UseSqlite($"Data Source={path};Pooling=False")
                    .Options
            ),
            "queue.db" => new QueueContext(
                new DbContextOptionsBuilder<QueueContext>()
                    .UseSqlite($"Data Source={path};Pooling=False")
                    .Options
            ),
            "app.db" => new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite($"Data Source={path};Pooling=False")
                    .Options
            ),
            _ => throw new InvalidDataException($"Unknown backup database: {name}"),
        };
        HashSet<string> known = context.Database.GetMigrations().ToHashSet();
        IEnumerable<string> applied = await context.Database.GetAppliedMigrationsAsync(
            cancellationToken
        );
        if (applied.Any(migration => !known.Contains(migration)))
            throw new InvalidOperationException($"Backup {name} has a newer or unknown schema.");
        await context.Database.MigrateAsync(cancellationToken);
    }

    private sealed record BackupManifest(string[] Databases, string[] ConfigFiles);
}
