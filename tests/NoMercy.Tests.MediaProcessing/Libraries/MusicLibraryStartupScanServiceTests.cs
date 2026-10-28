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

/// <summary>The server scans each music library once at start, and no other library.</summary>
[Trait("Category", "Unit")]
public sealed class MusicLibraryStartupScanServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public MusicLibraryStartupScanServiceTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;
        using MediaContext context = new(_options);
        context.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Only_the_music_libraries_are_queued_for_a_scan()
    {
        Library music = new() { Id = Ulid.NewUlid(), Title = "Music", Type = MediaTypes.MusicMediaType };
        Library movies = new() { Id = Ulid.NewUlid(), Title = "Movies", Type = MediaTypes.MovieMediaType };
        await using (MediaContext context = new(_options))
        {
            context.Libraries.AddRange(music, movies);
            await context.SaveChangesAsync();
        }

        Mock<IDbContextFactory<MediaContext>> factory = new();
        factory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(_options));
        Mock<JobDispatcher> dispatcher = new();

        MusicLibraryStartupScanService service = new(
            new Mock<IHostApplicationLifetime>().Object,
            factory.Object,
            dispatcher.Object,
            NullLogger<MusicLibraryStartupScanService>.Instance
        );

        await service.DispatchScansAsync(CancellationToken.None);

        dispatcher.Verify(d => d.DispatchJob<LibraryScanJob>(music.Id), Times.Once);
        dispatcher.Verify(d => d.DispatchJob<LibraryScanJob>(movies.Id), Times.Never);
    }
}
