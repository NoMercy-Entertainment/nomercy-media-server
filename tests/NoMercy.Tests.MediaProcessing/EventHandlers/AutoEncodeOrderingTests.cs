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
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Events;
using NoMercy.Events.Library;
using NoMercy.MediaProcessing.EventHandlers;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.Tests.MediaProcessing.EventHandlers;

/// <summary>
/// Scan events for one season arrive in whatever order the parallel import
/// workers finish. Auto-encode must queue them in episode order.
/// </summary>
public class AutoEncodeOrderingTests
{
    [Theory]
    [InlineData("Show S01E02", "Show S01E10", -1)]
    [InlineData("Show S01E10", "Show S01E9", 1)]
    [InlineData("show s01e05", "SHOW S01E05", 0)]
    [InlineData("Show S01E05", "Show S02E01", -1)]
    public void NaturalOrder_ComparesDigitRunsAsNumbers(string left, string right, int sign)
    {
        Math.Sign(NaturalStringComparer.Instance.Compare(left, right)).Should().Be(sign);
    }

    [Fact]
    public async Task ScansFinishingOutOfOrder_AreDispatchedInEpisodeOrder()
    {
        Ulid libraryId = Ulid.NewUlid();
        using SqliteConnection connection = new("DataSource=:memory:");
        IDbContextFactory<MediaContext> factory = ContextFactory(connection);
        Seed(
            factory,
            libraryId,
            (1, "/Show S01E01.mkv"),
            (6, "/Show S01E06.mkv"),
            (4, "/Show S01E04.mkv"),
            (2, "/Show S01E02.mkv"),
            (10, "/Show S01E10.mkv")
        );

        List<string> order = [];
        Mock<IJobDispatcher> dispatcher = new();
        dispatcher
            .Setup(d => d.Dispatch(It.IsAny<IShouldQueue>(), It.IsAny<string>(), It.IsAny<int>()))
            .Callback<IShouldQueue, string, int>(
                (job, _, _) =>
                {
                    if (job is VideoEncodeJob encodeJob)
                        order.Add(Path.GetFileNameWithoutExtension(encodeJob.InputFile));
                }
            );

        InMemoryEventBus bus = new();
        AutoEncodeSubscriber subscriber = new(
            bus,
            NullLogger<AutoEncodeSubscriber>.Instance,
            NoOpStorage(),
            factory,
            dispatcher.Object,
            batchQuietPeriod: TimeSpan.FromHours(1)
        );
        await subscriber.StartAsync(CancellationToken.None);

        foreach (int mediaId in new[] { 6, 1, 10, 4, 2 })
            await bus.PublishAsync(
                new MediaFilesScannedEvent { MediaId = mediaId, LibraryId = libraryId }
            );

        order.Should().BeEmpty("events are held until the batch is flushed");

        await subscriber.FlushAsync();

        order
            .Should()
            .Equal(
                "Show S01E01",
                "Show S01E02",
                "Show S01E04",
                "Show S01E06",
                "Show S01E10"
            );
    }

    [Fact]
    public async Task Stop_FlushesHeldJobs_NothingIsLost()
    {
        Ulid libraryId = Ulid.NewUlid();
        using SqliteConnection connection = new("DataSource=:memory:");
        IDbContextFactory<MediaContext> factory = ContextFactory(connection);
        Seed(factory, libraryId, (1, "/Show S01E01.mkv"));

        int dispatched = 0;
        Mock<IJobDispatcher> dispatcher = new();
        dispatcher
            .Setup(d => d.Dispatch(It.IsAny<IShouldQueue>(), It.IsAny<string>(), It.IsAny<int>()))
            .Callback(() => dispatched++);

        InMemoryEventBus bus = new();
        AutoEncodeSubscriber subscriber = new(
            bus,
            NullLogger<AutoEncodeSubscriber>.Instance,
            NoOpStorage(),
            factory,
            dispatcher.Object,
            batchQuietPeriod: TimeSpan.FromHours(1)
        );
        await subscriber.StartAsync(CancellationToken.None);
        await bus.PublishAsync(new MediaFilesScannedEvent { MediaId = 1, LibraryId = libraryId });
        dispatched.Should().Be(0);

        await subscriber.StopAsync(CancellationToken.None);

        dispatched.Should().Be(1, "a held job is queued on shutdown, never dropped");
    }

    private static IStorage NoOpStorage()
    {
        IStorageDriver driver = new LocalStorageDriver();
        return new LocalStorage(driver, new([], driver));
    }

    private static IDbContextFactory<MediaContext> ContextFactory(SqliteConnection connection)
    {
        connection.Open();
        using (SqliteCommand pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            pragma.ExecuteNonQuery();
        }

        DbContextOptions<MediaContext> options = new DbContextOptionsBuilder<MediaContext>()
            .UseSqlite(connection)
            .Options;

        using (MediaContext seed = new(options))
            seed.Database.EnsureCreated();

        Mock<IDbContextFactory<MediaContext>> factory = new();
        factory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(options));
        factory.Setup(f => f.CreateDbContext()).Returns(() => new(options));
        return factory.Object;
    }

    private static void Seed(
        IDbContextFactory<MediaContext> factory,
        Ulid libraryId,
        params (int MovieId, string Filename)[] files
    )
    {
        using MediaContext context = factory.CreateDbContext();

        Ulid folderId = Ulid.NewUlid();
        const string folderPath = "/media/shows";

        EncodingPreset preset = new()
        {
            Id = Ulid.NewUlid(),
            Name = "Archive 1080p",
            ProfileJson = "{}",
            IsBuiltIn = false,
        };
        context.EncodingPresets.Add(preset);
        context.Libraries.Add(
            new()
            {
                Id = libraryId,
                Title = "Shows",
                AutoEncodeOnScan = true,
                EncodePresetId = preset.Id,
            }
        );
        context.Folders.Add(
            new()
            {
                Id = folderId,
                Path = folderPath,
                DriverId = Ulid.NewUlid(),
            }
        );
        context.FolderLibrary.Add(new() { FolderId = folderId, LibraryId = libraryId });
        context.EncodingPresetFolders.Add(
            new()
            {
                PresetId = preset.Id,
                FolderId = folderId,
                IsDefault = true,
            }
        );

        foreach ((int movieId, string filename) in files)
            context.VideoFiles.Add(
                new()
                {
                    Id = Ulid.NewUlid(),
                    MovieId = movieId,
                    HostFolder = folderPath,
                    Filename = filename,
                    Quality = "1080p",
                    Share = "local",
                    Languages = "eng",
                }
            );

        context.SaveChanges();
    }
}
