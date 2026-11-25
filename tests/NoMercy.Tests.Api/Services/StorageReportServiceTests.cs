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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Api.Services;
using NoMercy.Data.ServerBackups;
using NoMercy.Database;
using NoMercy.Storage.Drivers.Local;
using Xunit;

namespace NoMercy.Tests.Api.Services;

[Trait("Category", "Unit")]
public sealed class StorageReportServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly Mock<IVolumeInspector> _volumes = new();

    public StorageReportServiceTests()
    {
        Directory.CreateDirectory(_root);
        _volumes.Setup(volumes => volumes.FreeBytesAt(It.IsAny<string>())).Returns(987_654_321L);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }

    private string Path_(params string[] parts) => Path.Combine([_root, .. parts]);

    private StorageLocations Locations() =>
        new(
            MediaDatabase: Path_("data", "media.db"),
            QueueDatabase: Path_("data", "queue.db"),
            AppDatabase: Path_("data", "app.db"),
            DataRoot: Path_("data"),
            BackupRoot: Path_("data", "backups"),
            ImagesPath: Path_("cache", "images"),
            LogPath: Path_("log"),
            TranscodePath: Path_("cache", "transcode"),
            TempPath: Path_("cache", "temp")
        );

    private static IDbContextFactory<TContext> FactoryFor<TContext>(string path)
        where TContext : DbContext
    {
        DbContextOptions<TContext> options = new DbContextOptionsBuilder<TContext>()
            .UseSqlite($"Data Source={path};Pooling=False")
            .Options;
        Mock<IDbContextFactory<TContext>> factory = new();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => (TContext)Activator.CreateInstance(typeof(TContext), options)!);
        return factory.Object;
    }

    private StorageReportService Service()
    {
        Directory.CreateDirectory(Path_("data"));
        using (
            QueueContext init = new(
                new DbContextOptionsBuilder<QueueContext>()
                    .UseSqlite($"Data Source={Path_("data", "queue.db")};Pooling=False")
                    .Options
            )
        )
            init.Database.EnsureCreated();
        using (
            MediaContext init = new(
                new DbContextOptionsBuilder<MediaContext>()
                    .UseSqlite($"Data Source={Path_("data", "media.db")};Pooling=False")
                    .Options
            )
        )
            init.Database.EnsureCreated();

        return new(
            new LocalStorageDriver(),
            Locations(),
            FactoryFor<MediaContext>(Path_("data", "media.db")),
            FactoryFor<QueueContext>(Path_("data", "queue.db")),
            _volumes.Object,
            NullLogger<StorageReportService>.Instance
        );
    }

    private void Place(int bytes, params string[] parts)
    {
        string path = Path_(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    private static long Bytes(StorageReport report, string id) =>
        report.Parts.Single(part => part.Id == id).Bytes;

    [Fact]
    public async Task ReadAsync_SumsTheKnownFilesPerPart()
    {
        StorageReportService service = Service();
        Place(1000, "data", "app.db");
        Place(3000, "data", "backups", "sets", "20261101-040000-0000000", "media.db");
        Place(2000, "data", "backups", "sets", "20261101-040000-0000000", "queue.db");
        Place(700, "data", "backups", "media.20261101040000.db");
        Place(300, "data", "media.db.bak-libraryTv-20260927-022300");
        Place(10, "cache", "images", "original", "a.jpg");
        Place(20, "cache", "images", "original", "b.jpg");
        Place(400, "cache", "images", "music", "folder", "c.jpg");
        Place(50, "cache", "images", "temp", "d.jpg");
        Place(7, "cache", "images", "w300", "e.jpg");
        Place(111, "log", "server.log");
        Place(222, "log", "old", "older.log");
        Place(5000, "cache", "transcode", "x", "seg.ts");
        Place(60, "cache", "temp", "t.bin");

        StorageReport report = await service.ReadAsync(CancellationToken.None);

        Bytes(report, "database_app").Should().Be(1000);
        Bytes(report, "backup_sets").Should().Be(5000);
        Bytes(report, "backup_copies").Should().Be(700);
        Bytes(report, "backup_hand_made").Should().Be(300);
        report.Parts.Single(part => part.Id == "backup_hand_made").Group.Should().Be("backups");
        Bytes(report, "image_original").Should().Be(30);
        Bytes(report, "image_music").Should().Be(400);
        Bytes(report, "image_temp").Should().Be(50);
        Bytes(report, "image_other").Should().Be(7);
        Bytes(report, "logs").Should().Be(333);
        Bytes(report, "transcode").Should().Be(5000);
        Bytes(report, "temp").Should().Be(60);
    }

    [Fact]
    public async Task ReadAsync_DatabasesReportTheirFileSizeAndTheFreePageBytes()
    {
        StorageReportService service = Service();
        await using (
            QueueContext context = new(
                new DbContextOptionsBuilder<QueueContext>()
                    .UseSqlite($"Data Source={Path_("data", "queue.db")};Pooling=False")
                    .Options
            )
        )
        {
            for (int i = 0; i < 200; i++)
                context.FailedJobs.Add(
                    new()
                    {
                        Uuid = Guid.NewGuid(),
                        Queue = "encoder",
                        Payload = new string('p', 3000),
                        Exception = "e",
                        FailedAt = DateTime.UtcNow,
                    }
                );
            await context.SaveChangesAsync();
            await context.FailedJobs.ExecuteDeleteAsync();
        }
        SqliteConnection.ClearAllPools();

        StorageReport report = await service.ReadAsync(CancellationToken.None);

        StoragePart queue = report.Parts.Single(part => part.Id == "database_queue");
        queue.Bytes.Should().Be(new FileInfo(Path_("data", "queue.db")).Length);
        queue.ReclaimableBytes.Should().BeGreaterThan(300_000).And.BeLessThan(queue.Bytes);
        report.Parts.Single(part => part.Id == "database_media").ReclaimableBytes.Should().Be(0);
    }

    [Fact]
    public async Task ReadAsync_FoldersThatDoNotExist_ReportZero()
    {
        StorageReportService service = Service();

        StorageReport report = await service.ReadAsync(CancellationToken.None);

        report
            .Parts.Where(part =>
                !part.Id.StartsWith("database_media") && !part.Id.StartsWith("database_queue")
            )
            .Select(part => part.Bytes)
            .Should()
            .AllBeEquivalentTo(0L);
    }

    [Fact]
    public async Task ReadAsync_ReportsTheFreeDiskAndATotalOfEveryPart()
    {
        StorageReportService service = Service();
        Place(1000, "data", "app.db");
        Place(60, "cache", "temp", "t.bin");

        StorageReport report = await service.ReadAsync(CancellationToken.None);

        report.FreeDiskBytes.Should().Be(987_654_321L);
        report.TotalBytes.Should().Be(report.Parts.Sum(part => part.Bytes));
        report.TotalBytes.Should().BeGreaterThanOrEqualTo(1060);
    }
}
