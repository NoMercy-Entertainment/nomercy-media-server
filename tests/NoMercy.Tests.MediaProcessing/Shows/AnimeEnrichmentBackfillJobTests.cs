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

using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NoMercy.Database;
using NoMercy.MediaProcessing.Movies;
using NoMercy.MediaProcessing.Shows;
using NoMercy.Providers.AniList;
using NoMercy.Providers.AniList.Models;
using NoMercy.Providers.Jikan;
using NoMercy.Providers.Jikan.Models;
using NoMercyQueue;

namespace NoMercy.Tests.MediaProcessing.Shows;

// The backfill is driven the way the queue drives it: each job gets its
// services through InjectStorageServices, runs one batch, and hands back the
// job that continues the run. A run ends when no follow-up job comes back; a
// new run is what the startup service dispatches on the next boot.
public class AnimeEnrichmentBackfillJobTests : IDisposable
{
    private const int ShowId = 1399;
    private const string ShowTitle = "Some Show";

    // A run that never ends is one of the failures these tests guard against,
    // so the drain gives up well past any batch count the seeded data needs.
    private const int MaxBatchesPerRun = 20;

    private readonly SqliteConnection _mediaConnection;
    private readonly DbContextOptions<MediaContext> _mediaOptions;
    private readonly DbContextOptions<AppDbContext> _appOptions;
    private readonly Mock<IAniListMetadataProvider> _aniList = new();
    private readonly Mock<IJikanMetadataProvider> _jikan = new();

    public AnimeEnrichmentBackfillJobTests()
    {
        _mediaConnection = new("Data Source=:memory:");
        _mediaConnection.Open();
        _mediaOptions = new DbContextOptionsBuilder<MediaContext>()
            .UseSqlite(_mediaConnection)
            .Options;
        using MediaContext context = new(_mediaOptions);
        context.Database.EnsureCreated();

        _appOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    public void Dispose()
    {
        _mediaConnection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void QueueName_IsExtras()
    {
        AnimeEnrichmentBackfillJob job = new(Mock.Of<IAnimeEnrichmentService>());

        job.QueueName.Should().Be("extras");
    }

    // Matches ShowExtrasJob/MovieExtrasJob (priority 1), not the queue's
    // absolute floor: priority 0 sorted this job strictly behind every other
    // extras-queue job forever, including ones enqueued after it - on a live
    // server the backlog never actually empties, so the backfill effectively
    // never got a second turn. Verified live: stuck at the same batch across
    // three server restarts until manually re-prioritized each time.
    [Fact]
    public void Priority_MatchesExtrasQueueSiblingsSoItIsNotStarvedForever()
    {
        AnimeEnrichmentBackfillJob job = new(Mock.Of<IAnimeEnrichmentService>());

        job.Priority.Should().Be(1);
    }

    [Fact]
    public async Task Handle_NoServicesInjected_DoesNotThrow()
    {
        // The queue's parameterless constructor path with no InjectStorageServices
        // call (e.g. a direct Handle() invocation in a test double) must no-op
        // rather than throw a NullReferenceException.
        AnimeEnrichmentBackfillJob job = new();

        Func<Task> act = () => job.Handle();

        await act.Should().NotThrowAsync();
    }

    // JobQueue.Enqueue drops a Dispatch() whose serialized payload matches an
    // already-present row (its own dedup check), and a self-redispatched
    // job's OWN row is still present - reserved, not yet deleted - while
    // Handle() runs (QueueWorker only deletes it in a finally block AFTER
    // Handle() returns). A parameterless AnimeEnrichmentBackfillJob() always
    // serializes identically, so self-redispatch always collided with its
    // own row: the insert silently no-op'd, the worker then deleted the old
    // row, and the backfill died with zero trace - reproduced live, twice.
    // DispatchedAfterTvCursor/MovieCursor exist purely to break that
    // collision: two batches at different cursor positions must serialize to
    // DIFFERENT payloads, or this bug is back.
    [Fact]
    public void SerializedPayload_DiffersAcrossCursorPositions_SoSelfRedispatchIsNeverDroppedAsADuplicate()
    {
        AnimeEnrichmentBackfillJob first = new() { DispatchedAfterTvCursor = 100 };
        AnimeEnrichmentBackfillJob second = new() { DispatchedAfterTvCursor = 125 };

        string firstPayload = SerializationHelper.Serialize(first);
        string secondPayload = SerializationHelper.Serialize(second);

        firstPayload.Should().NotBe(secondPayload);
        firstPayload.Should().Contain("100");
        secondPayload.Should().Contain("125");
    }

    // Same collision, the movie-cursor axis - both cursors advance
    // independently (a batch can process only tv, only movies, or both).
    [Fact]
    public void SerializedPayload_DiffersAcrossMovieCursorPositions()
    {
        AnimeEnrichmentBackfillJob first = new() { DispatchedAfterMovieCursor = 7 };
        AnimeEnrichmentBackfillJob second = new() { DispatchedAfterMovieCursor = 42 };

        SerializationHelper.Serialize(first).Should().NotBe(SerializationHelper.Serialize(second));
    }

    [Fact]
    public async Task Backfill_BothProvidersDown_IsNotMarkedComplete()
    {
        await SeedShowInATvLibraryAsync();
        ProvidersAreDown();

        await DrainOneRunAsync();

        (await IsCompleteAsync())
            .Should()
            .BeFalse("neither provider answered, so the show was never classified");
    }

    [Fact]
    public async Task Backfill_TitleTheProvidersCouldNotAnswer_IsRetriedOnTheNextRun()
    {
        await SeedShowInATvLibraryAsync();
        ProvidersAreDown();
        await DrainOneRunAsync();

        ProvidersAnswerNotAnime();
        await DrainOneRunAsync();

        _aniList.Verify(
            p => p.SearchAsync(ShowTitle, It.IsAny<int?>(), It.IsAny<bool?>()),
            Times.Exactly(2),
            "the show the providers could not answer must be asked again on the next run"
        );
        (await IsCompleteAsync())
            .Should()
            .BeTrue("the providers answered on the second run, so nothing is left to retry");
    }

    [Fact]
    public async Task Backfill_ProvidersStayDown_AsksOncePerRunAndEndsTheRun()
    {
        await SeedShowInATvLibraryAsync();
        ProvidersAreDown();

        await DrainOneRunAsync();
        await DrainOneRunAsync();

        _aniList.Verify(
            p => p.SearchAsync(ShowTitle, It.IsAny<int?>(), It.IsAny<bool?>()),
            Times.Exactly(2),
            "an outage costs one attempt per title per run, never a tight loop"
        );
        (await IsCompleteAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Backfill_ProvidersAnswerNotAnime_IsMarkedComplete()
    {
        await SeedShowInATvLibraryAsync();
        ProvidersAnswerNotAnime();

        await DrainOneRunAsync();

        (await IsCompleteAsync())
            .Should()
            .BeTrue("a title the providers positively answered is done");
    }

    private void ProvidersAreDown()
    {
        _aniList
            .Setup(p => p.SearchAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<bool?>()))
            .ThrowsAsync(new HttpRequestException("429", null, HttpStatusCode.TooManyRequests));
        _jikan
            .Setup(p => p.SearchAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<bool?>()))
            .ThrowsAsync(new HttpRequestException("504", null, HttpStatusCode.GatewayTimeout));
    }

    private void ProvidersAnswerNotAnime()
    {
        _aniList
            .Setup(p => p.SearchAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<bool?>()))
            .ReturnsAsync((AniListMedia?)null);
        _jikan
            .Setup(p => p.SearchAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<bool?>()))
            .ReturnsAsync((JikanAnime?)null);
    }

    private async Task DrainOneRunAsync()
    {
        IServiceProvider services = BuildServices();
        AnimeEnrichmentBackfillJob? job = new();
        int batches = 0;

        while (job is not null)
        {
            batches++;
            batches.Should().BeLessThanOrEqualTo(MaxBatchesPerRun, "a backfill run must end");

            job.InjectStorageServices(services);
            AnimeEnrichmentBackfillJob? next = await job.RunBatchAsync();

            // Round-trip through the queue's serializer, as a real redispatch does.
            job = next is null
                ? null
                : SerializationHelper.Deserialize<AnimeEnrichmentBackfillJob>(
                    SerializationHelper.Serialize(next)
                );
        }
    }

    private ServiceProvider BuildServices()
    {
        AnimeEnrichmentService enrichment = new(
            new MediaTypeClassifier(_aniList.Object, _jikan.Object),
            _aniList.Object,
            _jikan.Object,
            Mock.Of<IShowRepository>(),
            Mock.Of<IMovieRepository>()
        );

        ServiceCollection services = new();
        services.AddSingleton<IAnimeEnrichmentService>(enrichment);
        services.AddSingleton<IDbContextFactory<MediaContext>>(new MediaFactory(_mediaOptions));
        services.AddSingleton<IDbContextFactory<AppDbContext>>(new AppFactory(_appOptions));
        return services.BuildServiceProvider();
    }

    private async Task<bool> IsCompleteAsync()
    {
        await using AppDbContext appDb = new(_appOptions);
        return await AnimeEnrichmentBackfillState.IsCompleteAsync(appDb, CancellationToken.None);
    }

    private async Task SeedShowInATvLibraryAsync()
    {
        await using MediaContext context = new(_mediaOptions);
        Ulid libraryId = Ulid.NewUlid();
        context.Libraries.Add(
            new()
            {
                Id = libraryId,
                Title = "Television",
                Type = "tv",
            }
        );
        context.Tvs.Add(
            new()
            {
                Id = ShowId,
                Title = ShowTitle,
                LibraryId = libraryId,
            }
        );
        await context.SaveChangesAsync();
    }

    private sealed class MediaFactory(DbContextOptions<MediaContext> options)
        : IDbContextFactory<MediaContext>
    {
        public MediaContext CreateDbContext() => new(options);
    }

    private sealed class AppFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
