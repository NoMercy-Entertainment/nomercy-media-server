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

using Microsoft.EntityFrameworkCore;
using Moq;
using NoMercy.Database;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.Events;
using NoMercy.Events.Media;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.Providers.TMDB.Models.Movies;
using NoMercy.Providers.TMDB.Models.TV;

namespace NoMercy.Tests.MediaProcessing.Jobs;

public class MediaAddedImportJobTests
{
    [Fact]
    public async Task MovieImportTwice_PublishesMediaAddedOnce()
    {
        await using MediaContext context = CreateContext();
        Ulid firstLibraryId = Ulid.NewUlid();
        Ulid secondLibraryId = Ulid.NewUlid();
        MovieImportJob job = new() { Id = 533, LibraryId = firstLibraryId };
        Mock<IEventBus> bus = CreateEventBus();
        int addCount = 0;

        async Task<TmdbMovieAppends?> Add()
        {
            if (++addCount == 1)
            {
                context.LibraryMovie.Add(new(firstLibraryId, job.Id));
                await context.SaveChangesAsync();
            }

            return new TmdbMovieAppends { Id = job.Id, Title = "Thunderbolts*" };
        }

        await job.ImportAndPublishAsync(context, Add, bus.Object);
        job.LibraryId = secondLibraryId;
        await job.ImportAndPublishAsync(context, Add, bus.Object);

        VerifyOneMediaAdded(bus);
    }

    [Fact]
    public async Task ShowImportTwice_PublishesMediaAddedOnce()
    {
        await using MediaContext context = CreateContext();
        Ulid firstLibraryId = Ulid.NewUlid();
        Ulid secondLibraryId = Ulid.NewUlid();
        ShowImportJob job = new() { Id = 1396, LibraryId = firstLibraryId };
        Mock<IEventBus> bus = CreateEventBus();

        async Task<TmdbTvShowAppends?> Add()
        {
            if (!await context.LibraryTv.AnyAsync(link => link.TvId == job.Id))
            {
                context.LibraryTv.Add(new(firstLibraryId, job.Id));
                await context.SaveChangesAsync();
            }

            return new TmdbTvShowAppends { Id = job.Id, Name = "Example Show" };
        }

        await job.ImportAndPublishAsync(context, Add, bus.Object);
        job.LibraryId = secondLibraryId;
        await job.ImportAndPublishAsync(context, Add, bus.Object);

        VerifyOneMediaAdded(bus);
    }

    [Fact]
    public async Task MovieWithLegacyMovieLibraryId_DoesNotPublishMediaAdded()
    {
        await using MediaContext context = CreateContext();
        context.Movies.Add(
            new Movie
            {
                Id = 986056,
                LibraryId = Ulid.NewUlid(),
                Title = "Thunderbolts*",
            }
        );
        await context.SaveChangesAsync();

        MovieImportJob job = new() { Id = 986056, LibraryId = Ulid.NewUlid() };
        Mock<IEventBus> bus = CreateEventBus();
        await job.ImportAndPublishAsync(
            context,
            () =>
                Task.FromResult<TmdbMovieAppends?>(
                    new TmdbMovieAppends { Id = job.Id, Title = "Thunderbolts*" }
                ),
            bus.Object
        );

        bus.Verify(
            b => b.PublishAsync(It.IsAny<MediaAddedEvent>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task ShowWithLegacyTvLibraryId_DoesNotPublishMediaAdded()
    {
        await using MediaContext context = CreateContext();
        Ulid firstLibraryId = Ulid.NewUlid();
        context.Tvs.Add(
            new Tv
            {
                Id = 1396,
                LibraryId = firstLibraryId,
                Title = "Example Show",
            }
        );
        await context.SaveChangesAsync();

        ShowImportJob job = new() { Id = 1396, LibraryId = Ulid.NewUlid() };
        Mock<IEventBus> bus = CreateEventBus();
        await job.ImportAndPublishAsync(
            context,
            () =>
                Task.FromResult<TmdbTvShowAppends?>(
                    new TmdbTvShowAppends { Id = job.Id, Name = "Example Show" }
                ),
            bus.Object
        );

        bus.Verify(
            b => b.PublishAsync(It.IsAny<MediaAddedEvent>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    private static MediaContext CreateContext()
    {
        DbContextOptions<MediaContext> options = new DbContextOptionsBuilder<MediaContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new MediaContext(options);
    }

    private static Mock<IEventBus> CreateEventBus()
    {
        Mock<IEventBus> bus = new();
        bus.Setup(b => b.PublishAsync(It.IsAny<MediaAddedEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return bus;
    }

    private static void VerifyOneMediaAdded(Mock<IEventBus> bus) =>
        bus.Verify(
            b => b.PublishAsync(It.IsAny<MediaAddedEvent>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
}
