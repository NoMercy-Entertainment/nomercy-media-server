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

using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using NoMercy.Data.ServerBackups;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.Service.ServerBackups;

[Trait("Category", "Unit")]
public sealed class BackupServiceTests : IDisposable
{
    private readonly BackupTestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private BackupService CreateService(int retainCount = 5) =>
        BackupTestFolder.CreateBackupService(_folder.Options(retainCount));

    private string SetFolder(string id) => Path.Combine(_folder.BackupRoot, "sets", id);

    private BackupManifest ReadManifest(string id) =>
        JsonConvert.DeserializeObject<BackupManifest>(
            File.ReadAllText(Path.Combine(SetFolder(id), "manifest.json"))
        )!;

    private static string Sha256(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public async Task Create_CopiesEveryDatabaseAndEverySettingsFile()
    {
        _folder.CreateServer();

        BackupSetSummary backup = await CreateService()
            .CreateAsync("manual", CancellationToken.None);

        string set = SetFolder(backup.Id);
        foreach (string name in new[] { "app", "media", "queue" })
            BackupTestFolder
                .ReadMarker(Path.Combine(set, "databases", $"{name}.db"))
                .Should()
                .Be("original");
        File.ReadAllText(Path.Combine(set, "config", "settings.json"))
            .Should()
            .Be("{\"value\":\"original\"}");
        File.ReadAllText(Path.Combine(set, "config", "seeds", "libraries.jsonc"))
            .Should()
            .Be("// original");
    }

    [Fact]
    public async Task Create_WritesAManifestWithVersionMigrationsAndAChecksumPerFile()
    {
        _folder.CreateServer();

        BackupSetSummary backup = await CreateService()
            .CreateAsync("manual", CancellationToken.None);
        BackupManifest manifest = ReadManifest(backup.Id);

        manifest.FormatVersion.Should().Be(BackupManifest.CurrentFormatVersion);
        manifest.Id.Should().Be(backup.Id);
        manifest.Reason.Should().Be("manual");
        manifest.ServerVersion.Should().Be("1.2.3");
        manifest.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        manifest
            .Databases.Select(database => database.Name)
            .Should()
            .Equal("app", "media", "queue");
        manifest
            .Databases.Should()
            .OnlyContain(database => database.LastMigration == BackupTestFolder.SecondMigration);
        manifest
            .ConfigFiles.Select(file => file.Path)
            .Should()
            .Equal("config/seeds/libraries.jsonc", "config/settings.json");

        foreach (
            BackupManifestFile file in manifest.Databases.Concat<BackupManifestFile>(
                manifest.ConfigFiles
            )
        )
        {
            string path = Path.Combine(SetFolder(backup.Id), file.Path);
            file.Sha256.Should().Be(Sha256(path));
            file.Size.Should().Be(new FileInfo(path).Length);
        }
    }

    [Fact]
    public async Task Create_TheDatabaseCopiesStandAlone_WithNoSideFiles()
    {
        _folder.CreateServer();

        BackupSetSummary backup = await CreateService()
            .CreateAsync("manual", CancellationToken.None);

        Directory
            .GetFiles(Path.Combine(SetFolder(backup.Id), "databases"))
            .Select(path => Path.GetFileName(path))
            .Should()
            .BeEquivalentTo("app.db", "media.db", "queue.db");
    }

    [Fact]
    public async Task Create_SeesRowsThatAreStillInTheWal()
    {
        _folder.CreateServer();
        using SqliteConnection live = new($"Data Source={_folder.DatabasePath("media")}");
        live.Open();
        using (SqliteCommand command = live.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode=WAL; UPDATE marker SET content = 'in-wal';";
            command.ExecuteNonQuery();
        }
        File.Exists(_folder.DatabasePath("media") + "-wal").Should().BeTrue();

        BackupSetSummary backup = await CreateService()
            .CreateAsync("manual", CancellationToken.None);

        BackupTestFolder
            .ReadMarker(Path.Combine(SetFolder(backup.Id), "databases", "media.db"))
            .Should()
            .Be("in-wal");
    }

    [Fact]
    public async Task Create_WithADatabaseMissing_FailsAndLeavesNothingBehind()
    {
        _folder.CreateServer();
        File.Delete(_folder.DatabasePath("queue"));

        Func<Task> create = () => CreateService().CreateAsync("manual", CancellationToken.None);

        await create.Should().ThrowAsync<FileNotFoundException>();
        Directory
            .EnumerateFileSystemEntries(Path.Combine(_folder.BackupRoot, "sets"))
            .Should()
            .BeEmpty();
    }

    [Fact]
    public async Task Create_KeepsTheNewestSetsOnly()
    {
        _folder.CreateServer();
        BackupService service = CreateService(retainCount: 2);

        List<string> ids = [];
        for (int index = 0; index < 4; index++)
            ids.Add((await service.CreateAsync($"run {index}", CancellationToken.None)).Id);

        IReadOnlyList<BackupSetSummary> sets = await service.ListAsync(CancellationToken.None);
        sets.Select(set => set.Reason).Should().Equal("run 3", "run 2");
        Directory.Exists(SetFolder(ids[0])).Should().BeFalse();
        Directory.Exists(SetFolder(ids[1])).Should().BeFalse();
    }

    [Fact]
    public async Task Create_ClearsAHalfBuiltSetFromAnEarlierCrash()
    {
        _folder.CreateServer();
        string leftover = Path.Combine(
            _folder.BackupRoot,
            "sets",
            ".incomplete-20240101-000000-0000000"
        );
        Directory.CreateDirectory(leftover);
        File.WriteAllText(Path.Combine(leftover, "half.db"), "half");

        await CreateService().CreateAsync("manual", CancellationToken.None);

        Directory.Exists(leftover).Should().BeFalse();
    }

    [Fact]
    public async Task List_ShowsPublishedSetsNewestFirst_AndSkipsFoldersThatAreNotSets()
    {
        _folder.CreateServer();
        BackupService service = CreateService();
        BackupSetSummary first = await service.CreateAsync("first", CancellationToken.None);
        BackupSetSummary second = await service.CreateAsync("second", CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(_folder.BackupRoot, "sets", "not-a-set"));
        Directory.CreateDirectory(
            Path.Combine(_folder.BackupRoot, "sets", "20240101-000000-0000000")
        );

        IReadOnlyList<BackupSetSummary> sets = await service.ListAsync(CancellationToken.None);

        sets.Select(set => set.Id).Should().Equal(second.Id, first.Id);
        sets[0].ConfigFileCount.Should().Be(2);
        sets[0].SizeBytes.Should().BePositive();
    }

    [Fact]
    public async Task List_WithNoBackupsYet_IsEmpty()
    {
        IReadOnlyList<BackupSetSummary> sets = await CreateService()
            .ListAsync(CancellationToken.None);

        sets.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_WhenADatabaseCopyFailsItsCheck_PublishesNothing_AndKeepsTheOldSets()
    {
        _folder.CreateServer();
        BackupSetSummary good = await CreateService(retainCount: 1)
            .CreateAsync("good", CancellationToken.None);
        BackupService damaged = BackupTestFolder.CreateBackupService(
            _folder.Options(retainCount: 1),
            new FailingQuickCheck()
        );

        Func<Task> create = () => damaged.CreateAsync("damaged", CancellationToken.None);

        await create.Should().ThrowAsync<InvalidDataException>();
        IReadOnlyList<BackupSetSummary> sets = await damaged.ListAsync(CancellationToken.None);
        sets.Select(set => set.Id).Should().Equal(good.Id);
        Directory
            .EnumerateDirectories(Path.Combine(_folder.BackupRoot, "sets"))
            .Should()
            .ContainSingle();
    }

    [SkippableFact]
    public async Task Create_LimitsEveryFolderAndFileToItsOwner()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows applies the per-user profile ACL instead.");
        _folder.CreateServer();

        BackupSetSummary backup = await CreateService()
            .CreateAsync("manual", CancellationToken.None);

        if (OperatingSystem.IsWindows())
            return;
        const UnixFileMode folderMode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        const UnixFileMode fileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        string set = SetFolder(backup.Id);
        File.GetUnixFileMode(_folder.BackupRoot).Should().Be(folderMode);
        File.GetUnixFileMode(Path.Combine(_folder.BackupRoot, "sets")).Should().Be(folderMode);
        foreach (
            string folder in Directory
                .EnumerateDirectories(set, "*", SearchOption.AllDirectories)
                .Append(set)
        )
            File.GetUnixFileMode(folder).Should().Be(folderMode, folder);
        foreach (string file in Directory.EnumerateFiles(set, "*", SearchOption.AllDirectories))
            File.GetUnixFileMode(file).Should().Be(fileMode, file);
    }

    [Fact]
    public async Task Prune_KeepsTheSetsTheLastRestoreNamed()
    {
        _folder.CreateServer();
        BackupService service = CreateService(retainCount: 1);
        BackupOptions options = _folder.Options(retainCount: 1);
        BackupService roomy = CreateService(retainCount: 5);
        string safety = (await roomy.CreateAsync("safety", CancellationToken.None)).Id;
        string preRestore = (await roomy.CreateAsync("pre-restore", CancellationToken.None)).Id;
        Directory.CreateDirectory(options.RestoreRoot);
        File.WriteAllText(
            options.LastRestorePath,
            BackupFiles.Serialize(
                new RestoreResult
                {
                    Status = RestoreStatus.Applied,
                    SafetyBackupId = safety,
                    PreRestoreBackupId = preRestore,
                }
            )
        );

        await service.CreateAsync("later", CancellationToken.None);
        await service.CreateAsync("later again", CancellationToken.None);

        Directory.Exists(SetFolder(safety)).Should().BeTrue();
        Directory.Exists(SetFolder(preRestore)).Should().BeTrue();
        (await service.ListAsync(CancellationToken.None)).Should().HaveCount(3);
    }

    [Fact]
    public async Task Prune_WithAnUnreadableRestoreRequest_DeletesNothing()
    {
        _folder.CreateServer();
        BackupService service = CreateService(retainCount: 1);
        BackupOptions options = _folder.Options(retainCount: 1);
        string first = (await service.CreateAsync("first", CancellationToken.None)).Id;
        Directory.CreateDirectory(options.RestoreRoot);
        File.WriteAllText(options.PendingRestorePath, "{ not json");

        await service.CreateAsync("second", CancellationToken.None);

        Directory.Exists(SetFolder(first)).Should().BeTrue();
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("{\"status\":\"SomethingNew\"}")]
    public async Task Prune_SetsAnUnreadableLastResultAside_AndStillPrunes(string content)
    {
        _folder.CreateServer();
        BackupService service = CreateService(retainCount: 1);
        BackupOptions options = _folder.Options(retainCount: 1);
        string first = (await service.CreateAsync("first", CancellationToken.None)).Id;
        Directory.CreateDirectory(options.RestoreRoot);
        File.WriteAllText(options.LastRestorePath, content);
        string leftover = Path.Combine(options.SetsRoot, ".incomplete-20240101-000000-0000000");
        Directory.CreateDirectory(leftover);

        await service.CreateAsync("second", CancellationToken.None);

        Directory.Exists(SetFolder(first)).Should().BeFalse();
        Directory.Exists(leftover).Should().BeFalse();
        File.Exists(options.LastRestorePath).Should().BeFalse();
        File.ReadAllText(options.LastRestorePath + ".bad").Should().Be(content);
    }

    /// <summary>Real SQLite work, except that every copy fails its integrity check.</summary>
    private sealed class FailingQuickCheck : ISqliteFileOperations
    {
        private readonly SqliteFileOperations _inner = new();

        public void CopyOnline(string sourcePath, string destinationPath) =>
            _inner.CopyOnline(sourcePath, destinationPath);

        public void Seal(string databasePath) => _inner.Seal(databasePath);

        public IReadOnlyList<string> ReadAppliedMigrations(string databasePath) =>
            _inner.ReadAppliedMigrations(databasePath);

        public string? FindDamage(string databasePath) => "*** in database main *** Page 2: fake";

        public void ReleaseAllConnections() => _inner.ReleaseAllConnections();
    }
}
