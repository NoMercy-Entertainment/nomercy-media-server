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

using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.People;
using NoMercy.NmSystem.Information;
using NoMercy.Queue.MediaServer.Jobs;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;
using NoMercyQueue.Core.Interfaces;
using Xunit;

namespace NoMercy.Tests.Queue;

public class ImageCacheMaintenanceCronJobTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:;Foreign Keys=False");
    private readonly DbContextOptions<MediaContext> _options;
    private readonly Dictionary<string, string> _settingValues = [];
    private readonly string _folder = Path.Join(AppFiles.ImagesPath, "original");
    private readonly List<string> _placed = [];

    public ImageCacheMaintenanceCronJobTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;
        using MediaContext init = new(_options);
        init.Database.EnsureCreated();
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        foreach (string path in _placed)
            File.Delete(path);

        _connection.Dispose();
    }

    private IDbContextFactory<MediaContext> CreateFactory()
    {
        Mock<IDbContextFactory<MediaContext>> factory = new();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(_options));
        return factory.Object;
    }

    private ImageCacheMaintenanceCronJob CreateJob()
    {
        Mock<IConfigurationStore> store = new();
        store
            .Setup(x => x.GetValue(It.IsAny<string>()))
            .Returns((string key) => _settingValues.GetValueOrDefault(key));
        LocalStorageDriver driver = new();
        IStorage storage = new LocalStorage(
            driver,
            new StoragePathGuard([AppFiles.ImagesPath], driver)
        );
        IDbContextFactory<MediaContext> factory = CreateFactory();

        return new(
            new(store.Object),
            new(factory),
            new(storage, NullLogger<ImageCacheSweeper>.Instance),
            NullLogger<ImageCacheMaintenanceCronJob>.Instance
        );
    }

    private string Place(string name, int ageDays, int bytes = 10)
    {
        string path = Path.Join(_folder, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays));
        _placed.Add(path);
        return path;
    }

    private async Task SeedAsync(Action<MediaContext> seed)
    {
        await using MediaContext context = new(_options);
        seed(context);
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task ExecuteAsync_RemovesTheOrphanAndKeepsEveryImageAnItemRefersTo()
    {
        string tag = Guid.NewGuid().ToString("N");
        string orphan = Place($"{tag}-orphan.jpg", 60);
        string moviePoster = Place($"{tag}-movie.jpg", 60);
        string personProfile = Place($"{tag}-person.jpg", 60);
        string imageRow = Place($"{tag}-image.jpg", 60);
        await SeedAsync(context =>
        {
            context.Movies.Add(new() { Id = 9001, Poster = $"/{tag}-movie.jpg" });
            context.People.Add(new() { Id = 9002, Profile = $"/{tag}-person.jpg" });
            context.Images.Add(
                new()
                {
                    FilePath = $"/{tag}-image.jpg",
                    Type = "backdrop",
                    MovieId = 9001,
                }
            );
        });

        await CreateJob().ExecuteAsync(string.Empty);

        File.Exists(orphan).Should().BeFalse();
        File.Exists(moviePoster).Should().BeTrue();
        File.Exists(personProfile).Should().BeTrue();
        File.Exists(imageRow).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_OverTheConfiguredLimit_EvictsTheLeastRecentlyUsedImage()
    {
        string tag = Guid.NewGuid().ToString("N");
        const int sevenHundredKilobytes = 700 * 1024;
        string idle = Place($"{tag}-idle.jpg", 30, sevenHundredKilobytes);
        string served = Place($"{tag}-served.jpg", 1, sevenHundredKilobytes);
        await SeedAsync(context =>
        {
            context.Movies.Add(new() { Id = 9003, Poster = $"/{tag}-idle.jpg" });
            context.Movies.Add(new() { Id = 9004, Poster = $"/{tag}-served.jpg" });
        });
        _settingValues[ImageCacheSettings.MaxMegabytesKey] = "1";

        await CreateJob().ExecuteAsync(string.Empty);

        File.Exists(idle).Should().BeFalse();
        File.Exists(served).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_WhenTheDatabaseFails_RemovesNothing()
    {
        string orphanLooking = Place($"{Guid.NewGuid():N}.jpg", 60);
        Mock<IDbContextFactory<MediaContext>> broken = new();
        broken
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));
        Mock<IConfigurationStore> store = new();
        LocalStorageDriver driver = new();
        IStorage storage = new LocalStorage(
            driver,
            new StoragePathGuard([AppFiles.ImagesPath], driver)
        );
        ImageCacheMaintenanceCronJob job = new(
            new(store.Object),
            new(broken.Object),
            new(storage, NullLogger<ImageCacheSweeper>.Instance),
            NullLogger<ImageCacheMaintenanceCronJob>.Instance
        );

        await job.ExecuteAsync(string.Empty);

        File.Exists(orphanLooking).Should().BeTrue();
    }
}
