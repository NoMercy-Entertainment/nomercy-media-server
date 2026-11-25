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

namespace NoMercy.Tests.Service.ServerBackups;

[Trait("Category", "Unit")]
public sealed class BackupPruneNowTests : IDisposable
{
    private const string HandMade = "media.db.bak-libraryTv-20260927-022300";

    private readonly BackupTestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task PruneAsync_MoreSetsThanTheRetainCount_RemovesTheOldestAndReportsTheirBytes()
    {
        _folder.CreateServer();
        BackupService keepThree = BackupTestFolder.CreateBackupService(_folder.Options(3));
        BackupSetSummary oldest = await keepThree.CreateAsync("one", CancellationToken.None);
        BackupSetSummary middle = await keepThree.CreateAsync("two", CancellationToken.None);
        BackupSetSummary newest = await keepThree.CreateAsync("three", CancellationToken.None);
        long removedBytes = FolderBytes(SetFolder(oldest.Id)) + FolderBytes(SetFolder(middle.Id));
        BackupService keepOne = BackupTestFolder.CreateBackupService(_folder.Options(1));

        BackupPruneResult result = await keepOne.PruneAsync(CancellationToken.None);

        result.SetsRemoved.Should().Be(2);
        result.SetBytes.Should().Be(removedBytes).And.BeGreaterThan(0);
        (await keepOne.ListAsync(CancellationToken.None))
            .Select(set => set.Id)
            .Should()
            .BeEquivalentTo([newest.Id]);
    }

    [Fact]
    public async Task PruneAsync_AnOldLooseCopy_GoesAndItsBytesAreReported()
    {
        _folder.CreateServer();
        BackupService service = BackupTestFolder.CreateBackupService(_folder.Options(3));
        await service.CreateAsync("only", CancellationToken.None);
        string copy = Path.Combine(_folder.BackupRoot, "media.20200101000000.db");
        File.WriteAllBytes(copy, new byte[4321]);

        BackupPruneResult result = await service.PruneAsync(CancellationToken.None);

        File.Exists(copy).Should().BeFalse();
        result.CopiesRemoved.Should().Be(1);
        result.CopyBytes.Should().Be(4321);
        result.HandMadeRemoved.Should().Be(0);
        result.SetsRemoved.Should().Be(0);
    }

    [Fact]
    public async Task PruneAsync_AnOldHandMadeCopyWithTheFlag_GoesAndItsBytesAreReportedApart()
    {
        _folder.CreateServer();
        BackupService service = BackupTestFolder.CreateBackupService(_folder.Options(3));
        await service.CreateAsync("only", CancellationToken.None);
        string copy = Path.Combine(_folder.DataRoot, HandMade);
        File.WriteAllBytes(copy, new byte[4321]);
        File.SetLastWriteTimeUtc(copy, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        BackupPruneResult result = await service.PruneAsync(true, CancellationToken.None);

        File.Exists(copy).Should().BeFalse();
        result.HandMadeRemoved.Should().Be(1);
        result.HandMadeBytes.Should().Be(4321);
        result.CopiesRemoved.Should().Be(0);
        result.CopyBytes.Should().Be(0);
    }

    [Fact]
    public async Task PruneAsync_AnOldHandMadeCopyWithoutTheFlag_Stays()
    {
        _folder.CreateServer();
        BackupService service = BackupTestFolder.CreateBackupService(_folder.Options(3));
        await service.CreateAsync("only", CancellationToken.None);
        string copy = Path.Combine(_folder.DataRoot, HandMade);
        File.WriteAllBytes(copy, new byte[4321]);
        File.SetLastWriteTimeUtc(copy, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        BackupPruneResult result = await service.PruneAsync(CancellationToken.None);

        File.Exists(copy).Should().BeTrue();
        result.HandMadeRemoved.Should().Be(0);
    }

    [Fact]
    public async Task PruneAsync_NothingToRemove_ReportsZero()
    {
        _folder.CreateServer();
        BackupService service = BackupTestFolder.CreateBackupService(_folder.Options(3));
        await service.CreateAsync("only", CancellationToken.None);

        BackupPruneResult result = await service.PruneAsync(CancellationToken.None);

        result.Should().Be(new BackupPruneResult(0, 0, 0, 0));
    }

    private string SetFolder(string id) => Path.Combine(_folder.BackupRoot, "sets", id);

    private static long FolderBytes(string folder) =>
        Directory
            .GetFiles(folder, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);
}
