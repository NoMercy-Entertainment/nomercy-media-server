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
/// A restore is all or nothing. Every refusal and every failure below must leave
/// the live databases and settings byte for byte as they were.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RestoreApplierTests : IDisposable
{
    private readonly BackupTestFolder _folder = new();
    private readonly BackupOptions _options;

    public RestoreApplierTests()
    {
        _options = _folder.Options();
    }

    public void Dispose() => _folder.Dispose();

    // ── helpers ──────────────────────────────────────────────────────────────

    private static FakeBackupDatabaseSchema CurrentSchema() =>
        new(BackupTestFolder.FirstMigration, BackupTestFolder.SecondMigration);

    private RestoreApplier CreateApplier(IStorageDriver storage, IBackupDatabaseSchema schema)
    {
        SqliteFileOperations sqlite = new();
        return new(
            storage,
            sqlite,
            new BackupSetVerifier(storage, sqlite, schema, _options),
            schema,
            _options,
            TimeProvider.System,
            NullLogger<RestoreApplier>.Instance
        );
    }

    /// <summary>
    /// Backs up the server as "original", then changes every live database and
    /// the settings, so a restore has something to undo. Returns the backup id.
    /// </summary>
    private async Task<string> BackUpThenChangeServerAsync()
    {
        _folder.CreateServer("original");
        BackupService backups = BackupTestFolder.CreateBackupService(_options);
        string id = (await backups.CreateAsync("test", CancellationToken.None)).Id;

        foreach (string name in new[] { "app", "media", "queue" })
            Execute(_folder.DatabasePath(name), "UPDATE marker SET content = 'changed'");
        _folder.WriteConfig("settings.json", "{\"value\":\"changed\"}");
        _folder.WriteConfig("added-later.json", "{}");
        return id;
    }

    private void RequestRestore(string id) =>
        WriteFile(
            _options.PendingRestorePath,
            BackupFiles.Serialize(
                new PendingRestore { BackupId = id, RequestedAt = DateTime.UtcNow }
            )
        );

    private static void WriteFile(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static void Execute(string databasePath, string sql)
    {
        using SqliteConnection connection = new($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>The live state: the files beside the databases and the whole config folder.</summary>
    private Dictionary<string, byte[]> LiveState()
    {
        Dictionary<string, byte[]> state = Directory
            .EnumerateFiles(_folder.DataRoot)
            .ToDictionary(path => "data/" + Path.GetFileName(path), File.ReadAllBytes);
        foreach ((string relative, byte[] bytes) in BackupTestFolder.Snapshot(_folder.ConfigRoot))
            state["config/" + relative.Replace('\\', '/')] = bytes;
        return state;
    }

    /// <summary>The atomic writes of the journal and result rename a .tmp file; those are not swap steps.</summary>
    private static bool IsTemporary(string path) => path.EndsWith(".tmp", StringComparison.Ordinal);

    private string SetDatabase(string id, string name) =>
        Path.Combine(_options.SetsRoot, id, "databases", $"{name}.db");

    private void AssertNothingLeftBehind()
    {
        File.Exists(_options.PendingRestorePath).Should().BeFalse();
        Directory.Exists(_options.RestoreWorkRoot).Should().BeFalse();
    }

    private async Task<RestoreResult> ApplyAsync(
        IStorageDriver? storage = null,
        IBackupDatabaseSchema? schema = null,
        bool previousServerStopped = true
    )
    {
        RestoreResult? result = await CreateApplier(
                storage ?? new LocalStorageDriver(),
                schema ?? CurrentSchema()
            )
            .ApplyPendingAsync(previousServerStopped, CancellationToken.None);
        result.Should().NotBeNull();
        return result!;
    }

    // ── the round trip ───────────────────────────────────────────────────────

    [Fact]
    public async Task NothingPending_DoesNothing()
    {
        _folder.CreateServer();
        Dictionary<string, byte[]> before = LiveState();

        RestoreResult? result = await CreateApplier(new LocalStorageDriver(), CurrentSchema())
            .ApplyPendingAsync(previousServerStopped: true, CancellationToken.None);

        result.Should().BeNull();
        LiveState().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task RoundTrip_PutsEveryDatabaseAndTheSettingsBackAsTheyWereBackedUp()
    {
        string id = await BackUpThenChangeServerAsync();
        RequestRestore(id);

        RestoreResult result = await ApplyAsync();

        result.Status.Should().Be(RestoreStatus.Applied, result.Message);
        foreach (string name in new[] { "app", "media", "queue" })
            BackupTestFolder.ReadMarker(_folder.DatabasePath(name)).Should().Be("original");
        File.ReadAllText(Path.Combine(_folder.ConfigRoot, "settings.json"))
            .Should()
            .Be("{\"value\":\"original\"}");
        File.ReadAllText(Path.Combine(_folder.ConfigRoot, "seeds", "libraries.jsonc"))
            .Should()
            .Be("// original");
        File.Exists(Path.Combine(_folder.ConfigRoot, "added-later.json")).Should().BeFalse();
        AssertNothingLeftBehind();
        File.Exists(_options.LastRestorePath).Should().BeTrue();
        Directory.Exists(Path.Combine(_options.SetsRoot, id)).Should().BeTrue();
    }

    [Fact]
    public async Task RoundTrip_MovesAStaleWalOfTheLiveDatabaseAside()
    {
        string id = await BackUpThenChangeServerAsync();
        File.WriteAllText(_folder.DatabasePath("media") + "-wal", "stale wal");
        RequestRestore(id);

        RestoreResult result = await ApplyAsync();

        result.Status.Should().Be(RestoreStatus.Applied, result.Message);
        File.Exists(_folder.DatabasePath("media") + "-wal").Should().BeFalse();
        BackupTestFolder.ReadMarker(_folder.DatabasePath("media")).Should().Be("original");
    }

    [Fact]
    public async Task OlderSchema_IsMigratedForwardBeforeItGoesLive()
    {
        _folder.CreateServer("original");
        foreach (string name in new[] { "app", "media", "queue" })
            Execute(
                _folder.DatabasePath(name),
                $"DELETE FROM __EFMigrationsHistory WHERE MigrationId = '{BackupTestFolder.SecondMigration}'"
            );
        BackupService backups = BackupTestFolder.CreateBackupService(_options);
        string id = (await backups.CreateAsync("old", CancellationToken.None)).Id;
        RequestRestore(id);
        FakeBackupDatabaseSchema schema = CurrentSchema();

        RestoreResult result = await ApplyAsync(schema: schema);

        result.Status.Should().Be(RestoreStatus.Applied, result.Message);
        schema.Migrated.Should().BeEquivalentTo("app", "media", "queue");
        new SqliteFileOperations()
            .ReadAppliedMigrations(_folder.DatabasePath("media"))
            .Should()
            .Equal(BackupTestFolder.FirstMigration, BackupTestFolder.SecondMigration);
    }

    // ── refused before anything moves ────────────────────────────────────────

    [Fact]
    public async Task NewerSchema_IsRefused_AndTheLiveFilesAreUntouched()
    {
        string id = await BackUpThenChangeServerAsync();
        RequestRestore(id);
        Dictionary<string, byte[]> before = LiveState();

        RestoreResult result = await ApplyAsync(
            schema: new FakeBackupDatabaseSchema(BackupTestFolder.FirstMigration)
        );

        result.Status.Should().Be(RestoreStatus.Refused);
        result.Message.Should().Contain("newer server");
        LiveState().Should().BeEquivalentTo(before);
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task ADatabaseThatFailsItsChecksum_IsRefused_AndTheLiveFilesAreUntouched()
    {
        string id = await BackUpThenChangeServerAsync();
        Execute(SetDatabase(id, "media"), "UPDATE marker SET content = 'tampered'");
        RequestRestore(id);
        Dictionary<string, byte[]> before = LiveState();

        RestoreResult result = await ApplyAsync();

        result.Status.Should().Be(RestoreStatus.Refused);
        result.Message.Should().Contain("checksum");
        LiveState().Should().BeEquivalentTo(before);
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task ASettingsFileThatFailsItsChecksum_IsRefused_AndTheLiveFilesAreUntouched()
    {
        string id = await BackUpThenChangeServerAsync();
        File.WriteAllText(
            Path.Combine(_options.SetsRoot, id, "config", "settings.json"),
            "{\"value\":\"evil\"}"
        );
        RequestRestore(id);
        Dictionary<string, byte[]> before = LiveState();

        RestoreResult result = await ApplyAsync();

        result.Status.Should().Be(RestoreStatus.Refused);
        LiveState().Should().BeEquivalentTo(before);
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task AMissingFile_IsRefused_AndTheLiveFilesAreUntouched()
    {
        string id = await BackUpThenChangeServerAsync();
        File.Delete(SetDatabase(id, "queue"));
        RequestRestore(id);
        Dictionary<string, byte[]> before = LiveState();

        RestoreResult result = await ApplyAsync();

        result.Status.Should().Be(RestoreStatus.Refused);
        result.Message.Should().Contain("missing");
        LiveState().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task AManifestPathOutsideTheSet_IsRefused()
    {
        string id = await BackUpThenChangeServerAsync();
        string manifestPath = Path.Combine(_options.SetsRoot, id, BackupManifest.FileName);
        BackupManifest manifest = BackupFiles.Deserialize<BackupManifest>(
            File.ReadAllText(manifestPath)
        )!;
        manifest.ConfigFiles[0].Path = "config/../../../escape.json";
        File.WriteAllText(manifestPath, BackupFiles.Serialize(manifest));
        RequestRestore(id);
        Dictionary<string, byte[]> before = LiveState();

        RestoreResult result = await ApplyAsync();

        result.Status.Should().Be(RestoreStatus.Refused);
        LiveState().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task AFailedMigrationOfTheStagedCopy_IsRefused_AndTheLiveFilesAreUntouched()
    {
        string id = await BackUpThenChangeServerAsync();
        RequestRestore(id);
        Dictionary<string, byte[]> before = LiveState();
        FakeBackupDatabaseSchema schema = CurrentSchema();
        schema.FailFor = "queue";

        RestoreResult result = await ApplyAsync(schema: schema);

        result.Status.Should().Be(RestoreStatus.Refused);
        LiveState().Should().BeEquivalentTo(before);
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task WhileThePreviousServerStillRuns_IsRefused_AndTheRequestIsConsumed()
    {
        string id = await BackUpThenChangeServerAsync();
        RequestRestore(id);
        Dictionary<string, byte[]> before = LiveState();

        RestoreResult result = await ApplyAsync(previousServerStopped: false);

        result.Status.Should().Be(RestoreStatus.Refused);
        LiveState().Should().BeEquivalentTo(before);
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task ABackupThatNoLongerExists_IsRefused()
    {
        _folder.CreateServer();
        RequestRestore("20240101-000000-0000000");
        Dictionary<string, byte[]> before = LiveState();

        RestoreResult result = await ApplyAsync();

        result.Status.Should().Be(RestoreStatus.Refused);
        LiveState().Should().BeEquivalentTo(before);
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task AnUnreadableRequest_IsRefused_AndRemoved()
    {
        _folder.CreateServer();
        WriteFile(_options.PendingRestorePath, "{ not json");
        Dictionary<string, byte[]> before = LiveState();

        RestoreResult result = await ApplyAsync();

        result.Status.Should().Be(RestoreStatus.Refused);
        LiveState().Should().BeEquivalentTo(before);
        AssertNothingLeftBehind();
    }

    // ── failure in the middle of the swap ────────────────────────────────────

    /// <summary>
    /// The swap of this setup is nine renames: the stale -wal aside, then per
    /// database the live file aside and the staged one in, then the config folder
    /// aside and the staged one in. Each is made to fail in turn.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task AFailureAtAnyStepOfTheSwap_PutsEveryLiveFileBackByteForByte(int failingMove)
    {
        string id = await BackUpThenChangeServerAsync();
        File.WriteAllText(_folder.DatabasePath("media") + "-wal", "stale wal");
        RequestRestore(id);
        Dictionary<string, byte[]> before = LiveState();

        int moves = 0;
        FaultyStorageDriver storage = new()
        {
            FailMove = (from, _) => !IsTemporary(from) && moves++ == failingMove,
        };

        RestoreResult result = await ApplyAsync(storage);

        result.Status.Should().Be(RestoreStatus.RolledBack, result.Message);
        LiveState().Should().BeEquivalentTo(before);
        AssertNothingLeftBehind();
        File.Exists(Path.Combine(_options.RestoreWorkRoot, RestoreJournal.FileName))
            .Should()
            .BeFalse();
    }

    [Fact]
    public async Task TheSwapHasExactlyNineSteps_SoTheTheoryAboveCoversEveryOne()
    {
        string id = await BackUpThenChangeServerAsync();
        File.WriteAllText(_folder.DatabasePath("media") + "-wal", "stale wal");
        RequestRestore(id);
        FaultyStorageDriver storage = new();

        RestoreResult result = await ApplyAsync(storage);

        result.Status.Should().Be(RestoreStatus.Applied, result.Message);
        storage.Moves.Count(move => !IsTemporary(move.From)).Should().Be(9);
    }

    [Fact]
    public async Task WhenUndoingFailsToo_TheJournalIsKept_AndTheNextStartFinishesTheUndo()
    {
        string id = await BackUpThenChangeServerAsync();
        RequestRestore(id);
        Dictionary<string, byte[]> before = LiveState();
        string previousRoot = Path.Combine(_options.RestoreWorkRoot, "previous");

        // The swap fails half way, and the media database cannot be moved back:
        // the state a crash in the middle of the swap would leave.
        int moves = 0;
        FaultyStorageDriver broken = new()
        {
            FailMove = (from, _) =>
                !IsTemporary(from) && moves++ == 4
                || (
                    from.StartsWith(previousRoot, StringComparison.OrdinalIgnoreCase)
                    && from.EndsWith("media.db", StringComparison.Ordinal)
                ),
        };

        RestoreResult first = await ApplyAsync(broken);

        first.Status.Should().Be(RestoreStatus.RollbackIncomplete);
        File.Exists(Path.Combine(_options.RestoreWorkRoot, RestoreJournal.FileName))
            .Should()
            .BeTrue();
        File.Exists(_folder.DatabasePath("media")).Should().BeFalse();

        RestoreResult? second = await CreateApplier(new LocalStorageDriver(), CurrentSchema())
            .ApplyPendingAsync(previousServerStopped: true, CancellationToken.None);

        second!.Status.Should().Be(RestoreStatus.RolledBack, second.Message);
        LiveState().Should().BeEquivalentTo(before);
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task AnUnreadableJournal_StopsTheStart_AndTouchesNothing()
    {
        _folder.CreateServer();
        WriteFile(Path.Combine(_options.RestoreWorkRoot, RestoreJournal.FileName), "{ broken");
        Dictionary<string, byte[]> before = LiveState();

        RestoreResult result = await ApplyAsync();

        result.Status.Should().Be(RestoreStatus.RollbackIncomplete);
        LiveState().Should().BeEquivalentTo(before);
    }
}
