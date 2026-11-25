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

using NoMercy.Data.ServerBackups;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.Tests.Service.ServerBackups;

[Trait("Category", "Unit")]
public sealed class BackupHandMadeCopyTests : IDisposable
{
    private const string HandMade = "media.db.bak-libraryTv-20260927-022300";

    private readonly BackupTestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task HandMadeCopy_OlderThanEveryKeptSet_StaysWhileTheSettingIsOff()
    {
        _folder.CreateServer();
        WriteInDataFolder(HandMade, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        WriteInDataFolder(HandMade + "-wal", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        await Service().CreateAsync("daily scheduled backup", CancellationToken.None);

        FilesInDataFolder().Should().Contain([HandMade, HandMade + "-wal"]);
    }

    [Fact]
    public async Task HandMadeCopy_OlderThanEveryKeptSet_GoesWithItsSideFilesWhenTheSettingIsOn()
    {
        _folder.CreateServer();
        WriteInDataFolder(HandMade, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        WriteInDataFolder(HandMade + "-wal", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        await Service(pruneHandMade: true)
            .CreateAsync("daily scheduled backup", CancellationToken.None);

        FilesInDataFolder().Should().NotContain(name => name.StartsWith("media.db.bak-"));
    }

    [Fact]
    public async Task PruneAsync_WithTheFlag_RemovesTheHandMadeCopyThisRunOnly()
    {
        _folder.CreateServer();
        BackupService service = Service();
        await service.CreateAsync("only", CancellationToken.None);
        WriteInDataFolder(HandMade, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        BackupPruneResult withoutFlag = await service.PruneAsync(false, CancellationToken.None);
        FilesInDataFolder().Should().Contain(HandMade);
        BackupPruneResult withFlag = await service.PruneAsync(true, CancellationToken.None);

        withoutFlag.HandMadeRemoved.Should().Be(0);
        withFlag.HandMadeRemoved.Should().Be(1);
        withFlag.HandMadeBytes.Should().Be("a hand-made copy".Length);
        withFlag.CopiesRemoved.Should().Be(0);
        FilesInDataFolder().Should().NotContain(HandMade);
    }

    [Fact]
    public async Task PruneAsync_WithTheFlagAfterwardsOff_KeepsTheNextHandMadeCopy()
    {
        _folder.CreateServer();
        BackupService service = Service();
        await service.CreateAsync("only", CancellationToken.None);
        WriteInDataFolder(HandMade, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await service.PruneAsync(true, CancellationToken.None);
        WriteInDataFolder(HandMade, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        await service.PruneAsync(CancellationToken.None);

        FilesInDataFolder().Should().Contain(HandMade);
    }

    [Fact]
    public async Task HandMadeCopy_MadeSinceTheOldestKeptSet_Stays()
    {
        _folder.CreateServer();
        WriteInDataFolder(HandMade, new DateTime(2999, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        await Service().CreateAsync("daily scheduled backup", CancellationToken.None);

        FilesInDataFolder().Should().Contain(HandMade);
    }

    [Fact]
    public async Task FilesThatAreNotRecognisedBackups_StayWhateverTheirAge()
    {
        _folder.CreateServer();
        string[] notOurs =
        [
            "media.db.bak",
            "other.db.bak-libraryTv-20260927",
            "notes.bak-20260927",
            "media.db.backup-20260927",
            "media.dbx.bak-20260927",
        ];
        foreach (string name in notOurs)
            WriteInDataFolder(name, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        await Service().CreateAsync("daily scheduled backup", CancellationToken.None);

        FilesInDataFolder().Should().Contain(notOurs);
        File.Exists(_folder.DatabasePath("media")).Should().BeTrue();
    }

    [Fact]
    public async Task TheNewestBackupSetAlwaysStays()
    {
        _folder.CreateServer();
        WriteInDataFolder(HandMade, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        BackupService service = Service(retainCount: 1);

        await service.CreateAsync("older", CancellationToken.None);
        BackupSetSummary newest = await service.CreateAsync("newest", CancellationToken.None);

        (await service.ListAsync(CancellationToken.None))
            .Select(set => set.Id)
            .Should()
            .BeEquivalentTo([newest.Id]);
    }

    private BackupService Service(
        int retainCount = BackupOptions.DefaultRetainCount,
        bool pruneHandMade = false
    )
    {
        Mock<IConfigurationStore> store = new();
        store
            .Setup(x => x.GetValue(BackupSettings.PruneHandMadeCopiesKey))
            .Returns(pruneHandMade ? "true" : null);
        return BackupTestFolder.CreateBackupService(
            _folder.Options(retainCount),
            settings: new BackupSettings(store.Object)
        );
    }

    private void WriteInDataFolder(string name, DateTime lastWriteUtc)
    {
        string path = Path.Combine(_folder.DataRoot, name);
        File.WriteAllText(path, "a hand-made copy");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
    }

    private List<string> FilesInDataFolder() =>
        Directory.GetFiles(_folder.DataRoot).Select(path => Path.GetFileName(path)).ToList();
}
