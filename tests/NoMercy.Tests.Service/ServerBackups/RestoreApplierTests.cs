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

    private RestoreApplier CreateApplier(
        IStorageDriver storage,
        IBackupDatabaseSchema schema,
        IVolumeInspector? volumes = null,
        ISqliteFileOperations? sqliteFiles = null
    )
    {
        ISqliteFileOperations sqlite = sqliteFiles ?? new SqliteFileOperations();
        return new(
            storage,
            sqlite,
            new BackupSetVerifier(storage, sqlite, schema, _options),
            schema,
            BackupTestFolder.CreateWriter(_options, sqlite, storage),
            volumes ?? new DriveVolumeInspector(storage),
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

    /// <summary>
    /// Writes the request the way the backup service does: a safety set of the
    /// current state first, then the request naming it. Returns the safety id.
    /// </summary>
    private async Task<string> RequestRestoreAsync(string id)
    {
        string safetyId = (
            await BackupTestFolder
                .CreateBackupService(_options)
                .CreateAsync("safety", CancellationToken.None)
        ).Id;
        WriteFile(
            _options.PendingRestorePath,
            BackupFiles.Serialize(
                new PendingRestore
                {
                    BackupId = id,
                    RequestedAt = DateTime.UtcNow,
                    SafetyBackupId = safetyId,
                }
            )
        );
        return safetyId;
    }

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
        string? holdBackReason = null
    )
    {
        RestoreResult? result = await CreateApplier(
                storage ?? new LocalStorageDriver(),
                schema ?? CurrentSchema()
            )
            .ApplyPendingAsync(holdBackReason, CancellationToken.None);
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
            .ApplyPendingAsync(holdBackReason: null, CancellationToken.None);

        result.Should().BeNull();
        LiveState().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task RoundTrip_PutsEveryDatabaseAndTheSettingsBackAsTheyWereBackedUp()
    {
        string id = await BackUpThenChangeServerAsync();
        await RequestRestoreAsync(id);

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
        await RequestRestoreAsync(id);
        File.WriteAllText(_folder.DatabasePath("media") + "-wal", "stale wal");

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
        await RequestRestoreAsync(id);
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
        await RequestRestoreAsync(id);
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
        await RequestRestoreAsync(id);
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
        await RequestRestoreAsync(id);
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
        await RequestRestoreAsync(id);
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
        await RequestRestoreAsync(id);
        Dictionary<string, byte[]> before = LiveState();

        RestoreResult result = await ApplyAsync();

        result.Status.Should().Be(RestoreStatus.Refused);
        LiveState().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task AFailedMigrationOfTheStagedCopy_IsRefused_AndTheLiveFilesAreUntouched()
    {
        string id = await BackUpThenChangeServerAsync();
        await RequestRestoreAsync(id);
        Dictionary<string, byte[]> before = LiveState();
        FakeBackupDatabaseSchema schema = CurrentSchema();
        schema.FailFor = "queue";

        RestoreResult result = await ApplyAsync(schema: schema);

        result.Status.Should().Be(RestoreStatus.Refused);
        LiveState().Should().BeEquivalentTo(before);
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task WhileAnotherServerRuns_IsRefused_AndTheRequestIsKeptForTheNextStart()
    {
        string id = await BackUpThenChangeServerAsync();
        await RequestRestoreAsync(id);
        Dictionary<string, byte[]> before = LiveState();

        RestoreResult result = await ApplyAsync(
            holdBackReason: "Another server process holds the data folder."
        );

        result.Status.Should().Be(RestoreStatus.Refused);
        result.BackupId.Should().Be(id);
        LiveState().Should().BeEquivalentTo(before);
        File.Exists(_options.PendingRestorePath).Should().BeTrue();
        File.Exists(_options.LastRestorePath).Should().BeFalse();
        Directory.Exists(_options.RestoreWorkRoot).Should().BeFalse();

        RestoreResult next = await ApplyAsync();

        next.Status.Should().Be(RestoreStatus.Applied, next.Message);
    }

    [Fact]
    public async Task WhileAnotherServerRuns_ItsInterruptedSwapIsLeftAlone()
    {
        _folder.CreateServer();
        string journal = Path.Combine(_options.RestoreWorkRoot, RestoreJournal.FileName);
        WriteFile(journal, "{}");

        RestoreResult? result = await CreateApplier(new LocalStorageDriver(), CurrentSchema())
            .ApplyPendingAsync(
                holdBackReason: "Another server process holds the data folder.",
                CancellationToken.None
            );

        result.Should().BeNull();
        File.Exists(journal).Should().BeTrue();
    }

    [Fact]
    public async Task ABackupThatNoLongerExists_IsRefused()
    {
        _folder.CreateServer();
        await RequestRestoreAsync("20240101-000000-0000000");
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
        await RequestRestoreAsync(id);
        File.WriteAllText(_folder.DatabasePath("media") + "-wal", "stale wal");
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
        await RequestRestoreAsync(id);
        File.WriteAllText(_folder.DatabasePath("media") + "-wal", "stale wal");
        FaultyStorageDriver storage = new();

        RestoreResult result = await ApplyAsync(storage);

        result.Status.Should().Be(RestoreStatus.Applied, result.Message);
        storage
            .Moves.Count(move =>
                !IsTemporary(move.From)
                && !move.To.StartsWith(_options.SetsRoot, StringComparison.OrdinalIgnoreCase)
            )
            .Should()
            .Be(9);
    }

    [Fact]
    public async Task WhenUndoingFailsToo_TheJournalIsKept_AndTheNextStartFinishesTheUndo()
    {
        string id = await BackUpThenChangeServerAsync();
        await RequestRestoreAsync(id);
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
            .ApplyPendingAsync(holdBackReason: null, CancellationToken.None);

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

    // ── the safety net ───────────────────────────────────────────────────────

    [Fact]
    public async Task ARequestWithoutASafetyBackup_IsRefused_AndTheLiveFilesAreUntouched()
    {
        string id = await BackUpThenChangeServerAsync();
        WriteFile(
            _options.PendingRestorePath,
            BackupFiles.Serialize(
                new PendingRestore { BackupId = id, RequestedAt = DateTime.UtcNow }
            )
        );
        Dictionary<string, byte[]> before = LiveState();

        RestoreResult result = await ApplyAsync();

        result.Status.Should().Be(RestoreStatus.Refused);
        result.Message.Should().Contain("state before this restore is missing");
        LiveState().Should().BeEquivalentTo(before);
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task ARequestWhoseSafetyBackupIsGone_IsRefused_AndTheLiveFilesAreUntouched()
    {
        string id = await BackUpThenChangeServerAsync();
        string safetyId = await RequestRestoreAsync(id);
        Directory.Delete(Path.Combine(_options.SetsRoot, safetyId), recursive: true);
        Dictionary<string, byte[]> before = LiveState();

        RestoreResult result = await ApplyAsync();

        result.Status.Should().Be(RestoreStatus.Refused);
        LiveState().Should().BeEquivalentTo(before);
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task TheReplacedFiles_AreKeptAsARestorableSet_ThatThePruneLeavesAlone()
    {
        string id = await BackUpThenChangeServerAsync();
        await RequestRestoreAsync(id);
        FakeBackupDatabaseSchema schema = CurrentSchema();

        RestoreResult result = await ApplyAsync(schema: schema);

        result.Status.Should().Be(RestoreStatus.Applied, result.Message);
        string kept = result.PreRestoreBackupId!;
        kept.Should().NotBeNullOrEmpty();
        string keptFolder = Path.Combine(_options.SetsRoot, kept);
        BackupSetVerifier verifier = new(
            new LocalStorageDriver(),
            new SqliteFileOperations(),
            schema,
            _options
        );
        (await verifier.VerifyAsync(keptFolder, kept, CancellationToken.None))
            .IsValid.Should()
            .BeTrue();
        BackupTestFolder.ReadMarker(SetDatabase(kept, "media")).Should().Be("changed");
        File.Exists(Path.Combine(keptFolder, "config", "added-later.json")).Should().BeTrue();
        BackupFiles
            .Deserialize<BackupManifest>(
                File.ReadAllText(Path.Combine(keptFolder, BackupManifest.FileName))
            )!
            .Reason.Should()
            .Be($"before restore of {id}");

        // Two new backups with a retain count of one: the pre-restore set stays.
        BackupService backups = BackupTestFolder.CreateBackupService(
            _folder.Options(retainCount: 1)
        );
        await backups.CreateAsync("later", CancellationToken.None);
        await backups.CreateAsync("later again", CancellationToken.None);
        Directory.Exists(keptFolder).Should().BeTrue();
        Directory.Exists(Path.Combine(_options.SetsRoot, result.SafetyBackupId!)).Should().BeTrue();
    }

    [Fact]
    public async Task WhileItPrepares_AResultIsAlreadyRecorded_SoACrashThereStillLeavesOne()
    {
        string id = await BackUpThenChangeServerAsync();
        await RequestRestoreAsync(id);
        string? recordedDuringPrepare = null;
        SpyingSchema schema = new(() =>
            recordedDuringPrepare = File.ReadAllText(_options.LastRestorePath)
        );

        await ApplyAsync(schema: schema);

        recordedDuringPrepare.Should().Contain("stopped while preparing");
    }

    [Fact]
    public async Task FilesKeptFromAnEarlierRestore_AreNeverClearedAway()
    {
        string id = await BackUpThenChangeServerAsync();
        await RequestRestoreAsync(id);
        string leftover = Path.Combine(
            _options.RestoreWorkRoot,
            "previous",
            "databases",
            "media.db"
        );
        WriteFile(leftover, "the only copy of something");
        Dictionary<string, byte[]> before = LiveState();

        RestoreResult result = await ApplyAsync();

        result.Status.Should().Be(RestoreStatus.Refused);
        File.ReadAllText(leftover).Should().Be("the only copy of something");
        LiveState().Should().BeEquivalentTo(before);
    }

    // ── layout ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task AConfigFolderOnAnotherVolume_IsRefused_AndTheLiveFilesAreUntouched()
    {
        string id = await BackUpThenChangeServerAsync();
        await RequestRestoreAsync(id);
        Dictionary<string, byte[]> before = LiveState();
        FakeVolumes volumes = new(path =>
            path.StartsWith(_folder.ConfigRoot, StringComparison.OrdinalIgnoreCase)
                ? "/mnt/b/"
                : "/mnt/a/"
        );

        RestoreResult? result = await CreateApplier(
                new LocalStorageDriver(),
                CurrentSchema(),
                volumes
            )
            .ApplyPendingAsync(holdBackReason: null, CancellationToken.None);

        result!.Status.Should().Be(RestoreStatus.Refused);
        result.Message.Should().Contain("one volume");
        LiveState().Should().BeEquivalentTo(before);
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task AConfigFolderThatIsAMountPoint_IsRefused()
    {
        string id = await BackUpThenChangeServerAsync();
        await RequestRestoreAsync(id);
        Dictionary<string, byte[]> before = LiveState();
        FakeVolumes volumes = new(_ => "/mnt/a/") { MountPoint = _folder.ConfigRoot };

        RestoreResult? result = await CreateApplier(
                new LocalStorageDriver(),
                CurrentSchema(),
                volumes
            )
            .ApplyPendingAsync(holdBackReason: null, CancellationToken.None);

        result!.Status.Should().Be(RestoreStatus.Refused);
        result.Message.Should().Contain("mount point");
        LiveState().Should().BeEquivalentTo(before);
    }

    // ── more failure points ──────────────────────────────────────────────────

    [Fact]
    public async Task AFailureWhileStaging_IsRefused_AndTheLiveFilesAreUntouched()
    {
        string id = await BackUpThenChangeServerAsync();
        await RequestRestoreAsync(id);
        Dictionary<string, byte[]> before = LiveState();
        string staged = $"{Path.DirectorySeparatorChar}staged{Path.DirectorySeparatorChar}";
        FaultyStorageDriver storage = new() { FailOpenWrite = path => path.Contains(staged) };

        RestoreResult result = await ApplyAsync(storage);

        result.Status.Should().Be(RestoreStatus.Refused);
        result.Message.Should().Contain("not enough space");
        LiveState().Should().BeEquivalentTo(before);
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task AFailureAtTheCommitPoint_PutsEveryLiveFileBack()
    {
        string id = await BackUpThenChangeServerAsync();
        await RequestRestoreAsync(id);
        Dictionary<string, byte[]> before = LiveState();
        int journalDeletes = 0;
        FaultyStorageDriver storage = new()
        {
            FailDelete = path =>
                path.EndsWith(RestoreJournal.FileName, StringComparison.Ordinal)
                && journalDeletes++ == 0,
        };

        RestoreResult result = await ApplyAsync(storage);

        result.Status.Should().Be(RestoreStatus.RolledBack, result.Message);
        LiveState().Should().BeEquivalentTo(before);
        AssertNothingLeftBehind();
    }

    /// <summary>
    /// The process "dies" after N renames of the swap: from then on nothing on
    /// disk can change, so what is left is exactly what a crash would leave. A
    /// fresh start must put every live file back.
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
    [InlineData(9)]
    public async Task ACrashAfterAnyStepOfTheSwap_IsUndoneAtTheNextStart(int renamesBeforeCrash)
    {
        string id = await BackUpThenChangeServerAsync();
        await RequestRestoreAsync(id);
        File.WriteAllText(_folder.DatabasePath("media") + "-wal", "stale wal");
        Dictionary<string, byte[]> before = LiveState();
        FaultyStorageDriver dying = new() { DeadAfterSwapMoves = renamesBeforeCrash };

        RestoreResult crashed = await ApplyAsync(dying);

        crashed.Status.Should().NotBe(RestoreStatus.Applied);
        dying.SwapMoves.Should().Be(renamesBeforeCrash);

        RestoreResult? recovered = await CreateApplier(new LocalStorageDriver(), CurrentSchema())
            .ApplyPendingAsync(holdBackReason: null, CancellationToken.None);

        recovered!.Status.Should().Be(RestoreStatus.RolledBack, recovered.Message);
        LiveState().Should().BeEquivalentTo(before);
        AssertNothingLeftBehind();
    }

    // ── a live database with a hot WAL ───────────────────────────────────────

    /// <summary>
    /// Leaves the live media database as a crash would: rows committed to its
    /// -wal file that never reached the main file.
    /// </summary>
    private void LeaveAHotWal(string content)
    {
        string path = _folder.DatabasePath("media");
        byte[] main;
        byte[] wal;
        using (SqliteConnection connection = new($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; "
                + "UPDATE marker SET content = $content;";
            command.Parameters.AddWithValue("$content", content);
            command.ExecuteNonQuery();
            main = ReadShared(path);
            wal = ReadShared(path + "-wal");
        }

        SqliteConnection.ClearAllPools();
        File.WriteAllBytes(path, main);
        File.WriteAllBytes(path + "-wal", wal);
        if (File.Exists(path + "-shm"))
            File.Delete(path + "-shm");
    }

    private static byte[] ReadShared(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete
        );
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    [Fact]
    public async Task OverALiveDatabaseWithAHotWal_TheWalIsNotReplayedIntoTheBackup_AndItsRowsAreKept()
    {
        string id = await BackUpThenChangeServerAsync();
        await RequestRestoreAsync(id);
        LeaveAHotWal("in-hot-wal");
        File.Exists(_folder.DatabasePath("media") + "-wal").Should().BeTrue();

        RestoreResult result = await ApplyAsync();

        result.Status.Should().Be(RestoreStatus.Applied, result.Message);
        File.Exists(_folder.DatabasePath("media") + "-wal").Should().BeFalse();
        BackupTestFolder.ReadMarker(_folder.DatabasePath("media")).Should().Be("original");
        BackupTestFolder
            .ReadMarker(SetDatabase(result.PreRestoreBackupId!, "media"))
            .Should()
            .Be("in-hot-wal");
    }

    [Fact]
    public async Task OverALiveDatabaseWithAHotWal_ARollbackKeepsTheWalAndItsRows()
    {
        string id = await BackUpThenChangeServerAsync();
        await RequestRestoreAsync(id);
        LeaveAHotWal("in-hot-wal");
        Dictionary<string, byte[]> before = LiveState();
        FaultyStorageDriver storage = new()
        {
            FailMove = (from, _) => from.EndsWith("queue.db", StringComparison.Ordinal),
        };

        RestoreResult result = await ApplyAsync(storage);

        result.Status.Should().Be(RestoreStatus.RolledBack, result.Message);
        LiveState().Should().BeEquivalentTo(before);
        using SqliteConnection connection = new(
            $"Data Source={_folder.DatabasePath("media")};Pooling=False"
        );
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT content FROM marker";
        ((string)command.ExecuteScalar()!).Should().Be("in-hot-wal");
    }

    private sealed class FakeVolumes(Func<string, string> volumeOf) : IVolumeInspector
    {
        public string? MountPoint { get; init; }

        public string? VolumeOf(string path) => volumeOf(path);

        public bool IsMountPoint(string path) =>
            MountPoint is not null
            && Normalize(path).Equals(Normalize(MountPoint), StringComparison.OrdinalIgnoreCase);

        private static string Normalize(string path) =>
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
    }

    /// <summary>The current schema, calling back while the restore prepares.</summary>
    private sealed class SpyingSchema(Action whilePreparing) : IBackupDatabaseSchema
    {
        private readonly FakeBackupDatabaseSchema _inner = CurrentSchema();

        public IReadOnlyList<string> KnownMigrations(string databaseName) =>
            _inner.KnownMigrations(databaseName);

        public Task MigrateAsync(
            string databaseName,
            string databasePath,
            CancellationToken cancellationToken
        )
        {
            whilePreparing();
            return _inner.MigrateAsync(databaseName, databasePath, cancellationToken);
        }
    }

    // ── a damaged live database ──────────────────────────────────────────────

    /// <summary>
    /// A damaged database is exactly when an owner restores. The safety set
    /// keeps the damaged copy, flagged, instead of blocking the request, and the
    /// restore of a healthy backup goes ahead.
    /// </summary>
    [Fact]
    public async Task WithADamagedLiveDatabase_TheRequestSucceeds_ItsSafetySetIsFlagged_AndTheRestoreApplies()
    {
        string id = await BackUpThenChangeServerAsync();
        Execute(
            _folder.DatabasePath("media"),
            $"UPDATE marker SET content = '{MarkerDamageSqlite.CorruptMarker}'"
        );
        MarkerDamageSqlite sqlite = new();
        BackupService backups = BackupTestFolder.CreateBackupService(_options, sqlite);

        RestoreScheduleResult request = await backups.RequestRestoreAsync(
            id,
            CancellationToken.None
        );

        request.Status.Should().Be(RestoreScheduleStatus.Scheduled);
        string safetyId = request.Pending!.SafetyBackupId!;
        BackupManifest safety = ReadManifest(safetyId);
        BackupManifestDatabase media = safety.Databases.Single(database =>
            database.Name == "media"
        );
        media.Damaged.Should().BeTrue();
        media.Damage.Should().Be(MarkerDamageSqlite.Damage);
        safety
            .Databases.Where(database => database.Name != "media")
            .Should()
            .OnlyContain(database => !database.Damaged);

        // An ordinary backup of the same damaged server still fails, as before.
        Func<Task> ordinary = () => backups.CreateAsync("daily", CancellationToken.None);
        await ordinary.Should().ThrowAsync<InvalidDataException>();

        RestoreResult? result = await CreateApplier(
                new LocalStorageDriver(),
                CurrentSchema(),
                sqliteFiles: sqlite
            )
            .ApplyPendingAsync(holdBackReason: null, CancellationToken.None);

        result!.Status.Should().Be(RestoreStatus.Applied, result.Message);
        BackupTestFolder.ReadMarker(_folder.DatabasePath("media")).Should().Be("original");
        ReadManifest(result.PreRestoreBackupId!)
            .Databases.Single(database => database.Name == "media")
            .Damaged.Should()
            .BeTrue();
    }

    [Fact]
    public async Task ASetWithADamagedDatabase_CannotBeRestored()
    {
        _folder.CreateServer("original");
        Execute(
            _folder.DatabasePath("queue"),
            $"UPDATE marker SET content = '{MarkerDamageSqlite.CorruptMarker}'"
        );
        string flagged = (
            await BackupTestFolder
                .CreateWriter(_options, new MarkerDamageSqlite())
                .WriteAsync(
                    "before restoring something",
                    _options.Databases,
                    _options.ConfigRoot,
                    keepDamagedCopies: true,
                    CancellationToken.None
                )
        ).Id;
        await RequestRestoreAsync(flagged);
        Dictionary<string, byte[]> before = LiveState();

        RestoreResult result = await ApplyAsync();

        result.Status.Should().Be(RestoreStatus.Refused);
        result.Message.Should().Contain("marked damaged");
        LiveState().Should().BeEquivalentTo(before);
    }

    private BackupManifest ReadManifest(string id) =>
        BackupFiles.Deserialize<BackupManifest>(
            File.ReadAllText(Path.Combine(_options.SetsRoot, id, BackupManifest.FileName))
        )!;

    // ── after the commit point ───────────────────────────────────────────────

    private RestoreResult? LastResultOnDisk() =>
        BackupFiles.Deserialize<RestoreResult>(File.ReadAllText(_options.LastRestorePath));

    [Fact]
    public async Task ACrashBetweenTheCommitAndSavingTheReplacedFiles_IsFinishedAtTheNextStart()
    {
        string id = await BackUpThenChangeServerAsync();
        await RequestRestoreAsync(id);
        FaultyStorageDriver dying = new() { DeadAfterJournalDeleted = true };

        await ApplyAsync(dying);

        // What the crash leaves: the backup in place, its replaced files unsaved,
        // and a recorded result that says so instead of "nothing was changed".
        BackupTestFolder.ReadMarker(_folder.DatabasePath("media")).Should().Be("original");
        RestoreResult recorded = LastResultOnDisk()!;
        recorded.Status.Should().Be(RestoreStatus.Applied);
        recorded.Message.Should().Contain("had not finished");
        recorded.PreRestoreBackupId.Should().BeNull();

        RestoreResult? finished = await CreateApplier(new LocalStorageDriver(), CurrentSchema())
            .ApplyPendingAsync(holdBackReason: null, CancellationToken.None);

        finished!.Status.Should().Be(RestoreStatus.Applied, finished.Message);
        string kept = finished.PreRestoreBackupId!;
        BackupTestFolder.ReadMarker(SetDatabase(kept, "media")).Should().Be("changed");
        LastResultOnDisk()!.PreRestoreBackupId.Should().Be(kept);
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task WhenSavingTheReplacedFilesFails_EveryStartTriesAgain()
    {
        string id = await BackUpThenChangeServerAsync();
        await RequestRestoreAsync(id);
        FaultyStorageDriver full = new()
        {
            FailOpenWrite = path => path.Contains(BackupSetId.IncompletePrefix),
        };

        RestoreResult first = await ApplyAsync(full);

        first.Status.Should().Be(RestoreStatus.Applied, first.Message);
        first.PreRestoreBackupId.Should().BeNull();
        first.Message.Should().Contain("could not be saved");
        BackupTestFolder
            .ReadMarker(Path.Combine(_options.RestoreWorkRoot, "previous", "databases", "media.db"))
            .Should()
            .Be("changed");

        RestoreResult? retried = await CreateApplier(new LocalStorageDriver(), CurrentSchema())
            .ApplyPendingAsync(holdBackReason: null, CancellationToken.None);

        retried!.PreRestoreBackupId.Should().NotBeNull();
        BackupTestFolder
            .ReadMarker(SetDatabase(retried.PreRestoreBackupId!, "media"))
            .Should()
            .Be("changed");
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task FilesLeftAfterASuccessfulSave_AreClearedAway_AndDoNotBlockTheNextRestore()
    {
        string id = await BackUpThenChangeServerAsync();
        await RequestRestoreAsync(id);
        string previous = Path.Combine(_options.RestoreWorkRoot, "previous");
        FaultyStorageDriver stubborn = new()
        {
            FailDeleteDirectory = path =>
                path.Equals(previous, StringComparison.OrdinalIgnoreCase)
                || path.Equals(_options.RestoreWorkRoot, StringComparison.OrdinalIgnoreCase),
        };

        RestoreResult first = await ApplyAsync(stubborn);

        first.PreRestoreBackupId.Should().NotBeNull();
        Directory.Exists(previous).Should().BeTrue("the delete was made to fail");

        await RequestRestoreAsync(id);
        RestoreResult next = await ApplyAsync();

        next.Status.Should().Be(RestoreStatus.Applied, next.Message);
        AssertNothingLeftBehind();
    }

    // ── the work folder and the live modes ───────────────────────────────────

    [Fact]
    public void TheWorkFolder_IsInTheDataFolder_NotUnderTheBackups()
    {
        Path.GetDirectoryName(_options.RestoreWorkRoot).Should().Be(_folder.DataRoot);
        _options
            .RestoreWorkRoot.Should()
            .NotStartWith(_options.BackupRoot + Path.DirectorySeparatorChar);
    }

    [Fact]
    public async Task ABackupsFolderOnAnotherDisk_DoesNotStopARestore()
    {
        // The swap renames stay inside the data folder; reading the set from
        // another disk is an ordinary copy.
        string id = await BackUpThenChangeServerAsync();
        await RequestRestoreAsync(id);
        FakeVolumes volumes = new(path =>
            path.StartsWith(_options.BackupRoot, StringComparison.OrdinalIgnoreCase)
                ? "/mnt/b/"
                : "/mnt/a/"
        );

        RestoreResult? result = await CreateApplier(
                new LocalStorageDriver(),
                CurrentSchema(),
                volumes
            )
            .ApplyPendingAsync(holdBackReason: null, CancellationToken.None);

        result!.Status.Should().Be(RestoreStatus.Applied, result.Message);
    }

    [SkippableFact]
    public async Task TheRestoredFiles_KeepTheModesTheLiveOnesHad()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows has no Unix modes.");
        string id = await BackUpThenChangeServerAsync();
        await RequestRestoreAsync(id);
        if (OperatingSystem.IsWindows())
            return;
        const UnixFileMode groupReadableFolder =
            UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead
            | UnixFileMode.GroupExecute;
        const UnixFileMode groupReadableFile =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        const UnixFileMode worldReadableFile = groupReadableFile | UnixFileMode.OtherRead;
        string settings = Path.Combine(_folder.ConfigRoot, "settings.json");
        File.SetUnixFileMode(_folder.ConfigRoot, groupReadableFolder);
        File.SetUnixFileMode(settings, groupReadableFile);
        File.SetUnixFileMode(_folder.DatabasePath("media"), worldReadableFile);

        RestoreResult result = await ApplyAsync();

        result.Status.Should().Be(RestoreStatus.Applied, result.Message);
        File.GetUnixFileMode(_folder.ConfigRoot).Should().Be(groupReadableFolder);
        File.GetUnixFileMode(settings).Should().Be(groupReadableFile);
        File.GetUnixFileMode(_folder.DatabasePath("media")).Should().Be(worldReadableFile);
    }
}
