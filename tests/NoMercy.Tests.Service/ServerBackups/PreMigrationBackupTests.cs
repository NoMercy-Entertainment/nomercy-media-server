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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NoMercy.Data.ServerBackups;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.Service.ServerBackups;

[Trait("Category", "Unit")]
public sealed class PreMigrationBackupTests : IDisposable
{
    private readonly BackupTestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private PreMigrationBackup CreateBackup(int retainCount = 5, string? backupRoot = null) =>
        new(
            new LocalStorageDriver(),
            new SqliteFileOperations(),
            new BackupOptions
            {
                BackupRoot = backupRoot ?? _folder.BackupRoot,
                ConfigRoot = _folder.ConfigRoot,
                Databases = [],
                RetainCount = retainCount,
            },
            TimeProvider.System,
            NullLogger<PreMigrationBackup>.Instance
        );

    private string CreateDb(string name = "media")
    {
        string path = _folder.DatabasePath(name);
        BackupTestFolder.CreateDatabase(path, "fake sqlite content");
        return path;
    }

    private string[] Copies(string name) =>
        Directory.Exists(_folder.BackupRoot)
            ? Directory
                .GetFiles(_folder.BackupRoot)
                .Where(path =>
                    Path.GetFileName(path).StartsWith(name + ".", StringComparison.Ordinal)
                    && path.EndsWith(".db", StringComparison.Ordinal)
                )
                .ToArray()
            : [];

    private const string ProgressTemplate = "Backing up {Database}: {Done} of {Total} ({Percent}%)";

    private sealed class RecordingLogger : ILogger<PreMigrationBackup>
    {
        public List<(string Template, Dictionary<string, object?> Values)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            Dictionary<string, object?> values = [];
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
                foreach (KeyValuePair<string, object?> pair in pairs)
                    values[pair.Key] = pair.Value;
            Lines.Add(((string)values["{OriginalFormat}"]!, values));
        }

        public List<Dictionary<string, object?>> Progress() =>
            Lines
                .Where(line => line.Template == ProgressTemplate)
                .Select(line => line.Values)
                .ToList();
    }

    private sealed class ScriptedCopySqlite(Action<string, string> copy) : ISqliteFileOperations
    {
        private readonly SqliteFileOperations _inner = new();

        public void CopyOnline(string sourcePath, string destinationPath) =>
            copy(sourcePath, destinationPath);

        public void Seal(string databasePath) => _inner.Seal(databasePath);

        public IReadOnlyList<string> ReadAppliedMigrations(string databasePath) =>
            _inner.ReadAppliedMigrations(databasePath);

        public string? FindDamage(string databasePath) => _inner.FindDamage(databasePath);

        public void ReleaseAllConnections() => _inner.ReleaseAllConnections();
    }

    private PreMigrationBackup CreateScripted(
        Action<string, string> copy,
        FakeTimeProvider clock,
        RecordingLogger logger
    ) =>
        new(
            new LocalStorageDriver(),
            new ScriptedCopySqlite(copy),
            new BackupOptions
            {
                BackupRoot = _folder.BackupRoot,
                ConfigRoot = _folder.ConfigRoot,
                Databases = [],
            },
            clock,
            logger
        );

    private string CreateDbWithWal(string name, int walBytes)
    {
        string path = CreateDb(name);
        File.WriteAllBytes(path + "-wal", new byte[walBytes]);
        return path;
    }

    [Fact]
    public void BeforeTheCopy_LogsAStartLineWithZeroDoneAndTheDatabaseAndWalSize()
    {
        string path = CreateDbWithWal("media", 5);
        long total = new FileInfo(path).Length + 5;
        RecordingLogger logger = new();
        int progressWhenCopyStarted = -1;

        CreateScripted(
                (_, destination) =>
                {
                    progressWhenCopyStarted = logger.Progress().Count;
                    File.WriteAllText(destination, "x");
                },
                new FakeTimeProvider(),
                logger
            )
            .BackupBeforeMigration(path, 1)
            .Should()
            .BeTrue();

        progressWhenCopyStarted.Should().Be(1);
        Dictionary<string, object?> start = logger.Progress().First();
        start["Database"].Should().Be("media");
        start["Done"].Should().Be(0L);
        start["Total"].Should().Be(total);
        start["Percent"].Should().Be(0L);
    }

    [Fact]
    public void WhileTheCopyRuns_EveryTenSecondsLogsTheBackupFileSize()
    {
        string path = CreateDbWithWal("media", 5);
        long total = new FileInfo(path).Length + 5;
        FakeTimeProvider clock = new();
        RecordingLogger logger = new();

        CreateScripted(
                (_, destination) =>
                {
                    File.WriteAllBytes(destination, new byte[12]);
                    clock.Advance(TimeSpan.FromSeconds(10));
                },
                clock,
                logger
            )
            .BackupBeforeMigration(path, 1)
            .Should()
            .BeTrue();

        List<Dictionary<string, object?>> progress = logger.Progress();
        progress.Should().HaveCount(2);
        progress[1]["Done"].Should().Be(12L);
        progress[1]["Total"].Should().Be(total);
        progress[1]["Percent"].Should().Be(12 * 100 / total);
    }

    [Fact]
    public void NoProgressLineIsLoggedAfterTheCopyReturnsOrThrows()
    {
        FakeTimeProvider clock = new();
        RecordingLogger returned = new();
        CreateScripted((_, destination) => File.WriteAllText(destination, "x"), clock, returned)
            .BackupBeforeMigration(CreateDb("media"), 1);
        int afterReturn = returned.Progress().Count;
        clock.Advance(TimeSpan.FromSeconds(30));
        returned.Progress().Should().HaveCount(afterReturn);

        RecordingLogger thrown = new();
        CreateScripted((_, _) => throw new IOException("disk full"), clock, thrown)
            .BackupBeforeMigration(CreateDb("queue"), 1)
            .Should()
            .BeFalse();
        int afterThrow = thrown.Progress().Count;
        clock.Advance(TimeSpan.FromSeconds(30));
        thrown.Progress().Should().HaveCount(afterThrow);
    }

    [Fact]
    public void NoPendingMigrations_SkipsBackup()
    {
        bool result = CreateBackup().BackupBeforeMigration(CreateDb(), pendingMigrationCount: 0);

        result.Should().BeFalse();
        Directory.Exists(_folder.BackupRoot).Should().BeFalse();
    }

    [Fact]
    public void DatabaseDoesNotExist_SkipsBackup()
    {
        bool result = CreateBackup()
            .BackupBeforeMigration(_folder.DatabasePath("missing"), pendingMigrationCount: 3);

        result.Should().BeFalse();
        Directory.Exists(_folder.BackupRoot).Should().BeFalse();
    }

    [Fact]
    public void PendingMigrations_WritesATimestampedCopyWithTheSameContent()
    {
        bool result = CreateBackup().BackupBeforeMigration(CreateDb("queue"), 2);

        result.Should().BeTrue();
        string copy = Copies("queue").Should().ContainSingle().Subject;
        string[] parts = Path.GetFileName(copy).Split('.');
        parts.Should().HaveCount(3);
        parts[0].Should().Be("queue");
        parts[1].Should().MatchRegex("^[0-9]{14}$");
        parts[2].Should().Be("db");
        BackupTestFolder.ReadMarker(copy).Should().Be("fake sqlite content");
    }

    [Fact]
    public void TheCopyStandsAlone_WithNoSideFiles()
    {
        CreateBackup().BackupBeforeMigration(CreateDb(), 1);

        Directory
            .GetFiles(_folder.BackupRoot)
            .Should()
            .ContainSingle()
            .Which.Should()
            .EndWith(".db");
    }

    [Fact]
    public void WalResidentRow_IsInTheCopy()
    {
        string path = _folder.DatabasePath("wal-media");
        using (SqliteConnection connection = new($"Data Source={path}"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "PRAGMA journal_mode=WAL; CREATE TABLE marker (content TEXT); "
                + "INSERT INTO marker (content) VALUES ('wal-resident-row');";
            command.ExecuteNonQuery();
            File.Exists(path + "-wal").Should().BeTrue();

            CreateBackup().BackupBeforeMigration(path, 1).Should().BeTrue();
        }

        SqliteConnection.ClearAllPools();
        BackupTestFolder.ReadMarker(Copies("wal-media").Single()).Should().Be("wal-resident-row");
    }

    [Fact]
    public void InvalidBackupRoot_ReturnsFalseWithoutThrowing()
    {
        bool result = CreateBackup(backupRoot: "\0invalid\0path")
            .BackupBeforeMigration(CreateDb(), 1);

        result.Should().BeFalse();
    }

    [Fact]
    public void AboveTheRetainCount_PrunesTheOldest()
    {
        string path = CreateDb("app");
        Directory.CreateDirectory(_folder.BackupRoot);
        for (int index = 1; index <= 4; index++)
            File.WriteAllText(
                Path.Combine(_folder.BackupRoot, $"app.2026010100000{index}.db"),
                "old"
            );

        CreateBackup(retainCount: 3).BackupBeforeMigration(path, 1);

        string[] names = Copies("app").Select(copy => Path.GetFileName(copy)).ToArray();
        names.Should().HaveCount(3);
        names.Should().NotContain("app.20260101000001.db");
        names.Should().NotContain("app.20260101000002.db");
    }

    [Fact]
    public void BelowTheRetainCount_KeepsAll()
    {
        string path = CreateDb();
        Directory.CreateDirectory(_folder.BackupRoot);
        File.WriteAllText(Path.Combine(_folder.BackupRoot, "media.20260101000001.db"), "old");
        File.WriteAllText(Path.Combine(_folder.BackupRoot, "media.20260101000002.db"), "old");

        CreateBackup(retainCount: 5).BackupBeforeMigration(path, 1);

        Copies("media").Should().HaveCount(3);
    }

    [Fact]
    public void EachDatabase_IsPrunedOnItsOwn()
    {
        string media = CreateDb("media");
        string queue = CreateDb("queue");
        Directory.CreateDirectory(_folder.BackupRoot);
        for (int index = 1; index <= 3; index++)
        {
            File.WriteAllText(
                Path.Combine(_folder.BackupRoot, $"media.2026010100000{index}.db"),
                "old"
            );
            File.WriteAllText(
                Path.Combine(_folder.BackupRoot, $"queue.2026010100000{index}.db"),
                "old"
            );
        }

        PreMigrationBackup backup = CreateBackup(retainCount: 2);
        backup.BackupBeforeMigration(media, 1);
        backup.BackupBeforeMigration(queue, 1);

        Copies("media").Should().HaveCount(2);
        Copies("queue").Should().HaveCount(2);
    }

    // #670: a pruned copy used to leave its -wal and -shm files behind.
    [Fact]
    public void Prune_RemovesTheSideFilesOfPrunedCopies_AndKeepsThoseOfKeptCopies()
    {
        string path = CreateDb();
        Directory.CreateDirectory(_folder.BackupRoot);
        for (int index = 1; index <= 4; index++)
        {
            string copy = Path.Combine(_folder.BackupRoot, $"media.2026010100000{index}.db");
            File.WriteAllText(copy, "old");
            File.WriteAllText(copy + "-wal", "wal");
            File.WriteAllText(copy + "-shm", "shm");
        }

        CreateBackup(retainCount: 3).BackupBeforeMigration(path, 1);

        string[] names = Directory
            .GetFiles(_folder.BackupRoot)
            .Select(file => Path.GetFileName(file))
            .ToArray();
        names.Should().NotContain(name => name.StartsWith("media.20260101000001.db"));
        names.Should().NotContain(name => name.StartsWith("media.20260101000002.db"));
        foreach (int kept in new[] { 3, 4 })
        {
            names.Should().Contain($"media.2026010100000{kept}.db-wal");
            names.Should().Contain($"media.2026010100000{kept}.db-shm");
        }
    }

    [Fact]
    public void Prune_RemovesSideFilesWhoseCopyIsAlreadyGone()
    {
        string path = CreateDb();
        Directory.CreateDirectory(_folder.BackupRoot);
        string orphan = Path.Combine(_folder.BackupRoot, "media.20251005000000.db");
        File.WriteAllText(orphan + "-wal", "wal");
        File.WriteAllText(orphan + "-shm", "shm");
        File.WriteAllText(Path.Combine(_folder.BackupRoot, "queue.20251005000000.db-wal"), "wal");

        CreateBackup().BackupBeforeMigration(path, 1);

        File.Exists(orphan + "-wal").Should().BeFalse();
        File.Exists(orphan + "-shm").Should().BeFalse();
        // Another database's files are left to that database's own prune.
        File.Exists(Path.Combine(_folder.BackupRoot, "queue.20251005000000.db-wal"))
            .Should()
            .BeTrue();
    }
}
