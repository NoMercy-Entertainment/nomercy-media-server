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

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Data.ServerBackups;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.Service.ServerBackups;

/// <summary>
/// A throwaway server layout in the temp folder: a data folder with real SQLite
/// databases, a config folder with a nested file, and a backups folder. Nothing
/// here touches the real NoMercy folders.
/// </summary>
public sealed class BackupTestFolder : IDisposable
{
    public const string FirstMigration = "20240101000000_Initial";
    public const string SecondMigration = "20240201000000_AddThings";

    public BackupTestFolder()
    {
        Root = Path.Combine(Path.GetTempPath(), $"nm_backup_{Guid.NewGuid():N}");
        DataRoot = Path.Combine(Root, "data");
        ConfigRoot = Path.Combine(Root, "config");
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(ConfigRoot);
    }

    public string Root { get; }
    public string DataRoot { get; }
    public string ConfigRoot { get; }
    public string BackupRoot => Path.Combine(DataRoot, "backups");

    public string DatabasePath(string name) => Path.Combine(DataRoot, $"{name}.db");

    public BackupOptions Options(int retainCount = BackupOptions.DefaultRetainCount) =>
        new()
        {
            BackupRoot = BackupRoot,
            DataRoot = DataRoot,
            ConfigRoot = ConfigRoot,
            Databases =
            [
                new("app", DatabasePath("app")),
                new("media", DatabasePath("media")),
                new("queue", DatabasePath("queue")),
            ],
            RetainCount = retainCount,
            ServerVersion = "1.2.3",
        };

    /// <summary>
    /// Creates the three databases with a migration history and one row each, and
    /// two settings files, one of them in a subfolder.
    /// </summary>
    public void CreateServer(string content = "original")
    {
        foreach (string name in new[] { "app", "media", "queue" })
            CreateDatabase(DatabasePath(name), content, FirstMigration, SecondMigration);

        WriteConfig("settings.json", $"{{\"value\":\"{content}\"}}");
        WriteConfig("seeds/libraries.jsonc", $"// {content}");
    }

    public void WriteConfig(string relativePath, string text)
    {
        string path = Path.Combine(ConfigRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public static void CreateDatabase(string path, string content, params string[] migrations)
    {
        using SqliteConnection connection = new($"Data Source={path};Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE marker (content TEXT NOT NULL);"
            + "CREATE TABLE __EFMigrationsHistory (MigrationId TEXT PRIMARY KEY, ProductVersion TEXT NOT NULL);";
        command.ExecuteNonQuery();

        command.CommandText = "INSERT INTO marker (content) VALUES ($content)";
        command.Parameters.AddWithValue("$content", content);
        command.ExecuteNonQuery();

        foreach (string migration in migrations)
        {
            command.Parameters.Clear();
            command.CommandText =
                "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ($id, '10.0.0')";
            command.Parameters.AddWithValue("$id", migration);
            command.ExecuteNonQuery();
        }
    }

    public static string ReadMarker(string path)
    {
        using SqliteConnection connection = new($"Data Source={path};Pooling=False;Mode=ReadOnly");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT content FROM marker";
        return (string)command.ExecuteScalar()!;
    }

    /// <summary>Every file under <paramref name="folder"/> with its bytes, keyed by relative path.</summary>
    public static Dictionary<string, byte[]> Snapshot(string folder) =>
        Directory
            .EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(folder, path), File.ReadAllBytes);

    public static BackupSetWriter CreateWriter(
        BackupOptions options,
        ISqliteFileOperations? sqlite = null,
        IStorageDriver? storage = null
    ) =>
        new(
            storage ?? new LocalStorageDriver(),
            sqlite ?? new SqliteFileOperations(),
            options,
            TimeProvider.System,
            NullLogger<BackupSetWriter>.Instance
        );

    public static BackupService CreateBackupService(
        BackupOptions options,
        ISqliteFileOperations? sqlite = null,
        IStorageDriver? storage = null,
        IBackupSetWriter? writer = null,
        IVolumeInspector? volumes = null
    ) =>
        new(
            storage ?? new LocalStorageDriver(),
            writer ?? CreateWriter(options, sqlite, storage),
            options,
            TimeProvider.System,
            NullLogger<BackupService>.Instance,
            volumes
        );

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}
