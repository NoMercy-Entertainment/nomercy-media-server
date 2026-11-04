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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.MediaProcessing.Libraries;
using NoMercy.NmSystem.Domain;

namespace NoMercy.Tests.MediaProcessing.Libraries;

/// <summary>The server scans every library once at start, not only the music ones.</summary>
[Trait("Category", "Unit")]
public sealed class LibraryStartupScanServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public LibraryStartupScanServiceTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;
        using MediaContext context = new(_options);
        context.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Every_library_type_is_queued_for_a_scan()
    {
        Library music = new()
        {
            Id = Ulid.NewUlid(),
            Title = "Music",
            Type = MediaTypes.MusicMediaType,
        };
        Library movies = new()
        {
            Id = Ulid.NewUlid(),
            Title = "Movies",
            Type = MediaTypes.MovieMediaType,
        };
        Library tv = new()
        {
            Id = Ulid.NewUlid(),
            Title = "TV Shows",
            Type = MediaTypes.TvMediaType,
        };
        Library anime = new()
        {
            Id = Ulid.NewUlid(),
            Title = "Anime",
            Type = MediaTypes.AnimeMediaType,
        };
        await using (MediaContext context = new(_options))
        {
            context.Libraries.AddRange(music, movies, tv, anime);
            await context.SaveChangesAsync();
        }

        Mock<IDbContextFactory<MediaContext>> factory = new();
        factory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(_options));
        Mock<JobDispatcher> dispatcher = new();

        LibraryStartupScanService service = new(
            new Mock<IHostApplicationLifetime>().Object,
            factory.Object,
            dispatcher.Object,
            NullLogger<LibraryStartupScanService>.Instance
        );

        await service.DispatchScansAsync(CancellationToken.None);

        dispatcher.Verify(d => d.DispatchJob<LibraryScanJob>(music.Id), Times.Once);
        dispatcher.Verify(d => d.DispatchJob<LibraryScanJob>(movies.Id), Times.Once);
        dispatcher.Verify(d => d.DispatchJob<LibraryScanJob>(tv.Id), Times.Once);
        dispatcher.Verify(d => d.DispatchJob<LibraryScanJob>(anime.Id), Times.Once);
    }
}
