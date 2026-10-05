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
using Moq;
using NoMercy.Api.EventHandlers;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.TvShows;
using NoMercy.Events;
using NoMercy.Events.Library;
using NoMercy.Events.Onboarding;
using NoMercy.OpticalMedia.Onboarding;
using Xunit;

namespace NoMercy.Tests.Api.EventHandlers;

[Trait("Category", "Unit")]
public class DiscOnboardingCompletionEventHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _dbOptions;

    public DiscOnboardingCompletionEventHandlerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext seedContext = new(_dbOptions);
        seedContext.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private IDbContextFactory<MediaContext> ContextFactory => new TestDbContextFactory(_dbOptions);

    private static Mock<IEventBus> SubscribableEventBus() =>
        new Mock<IEventBus>().Also(bus =>
            bus.Setup(b =>
                    b.Subscribe<MediaFilesScannedEvent>(
                        It.IsAny<Func<MediaFilesScannedEvent, CancellationToken, Task>>()
                    )
                )
                .Returns(Mock.Of<IDisposable>())
        );

    [Fact]
    public async Task OnMediaFilesScanned_MovieMatchesConfirmedTmdbId_CompletesSessionAndBroadcasts()
    {
        Ulid libraryId = Ulid.NewUlid();
        DiscOnboardingSessionStore store = new();
        store.Set(
            DiscOnboardingSession
                .Create("D:\\")
                .WithJob("job-1", libraryId, confirmedTmdbId: 27205, confirmedMediaType: "movie")
        );

        Mock<IEventBus> eventBus = SubscribableEventBus();
        DiscOnboardingStateChangedEvent? published = null;
        eventBus
            .Setup(b =>
                b.PublishAsync(
                    It.IsAny<DiscOnboardingStateChangedEvent>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<DiscOnboardingStateChangedEvent, CancellationToken>(
                (evt, _) => published = evt
            )
            .Returns(Task.CompletedTask);

        using DiscOnboardingCompletionEventHandler handler = new(
            eventBus.Object,
            store,
            ContextFactory
        );

        await handler.OnMediaFilesScanned(
            new() { MediaId = 27205, LibraryId = libraryId },
            CancellationToken.None
        );

        published.Should().NotBeNull();
        published!.StateData.State.Should().Be(nameof(DiscOnboardingState.Complete));
        published.StateData.ResultType.Should().Be("movie");
        published.StateData.ResultId.Should().Be("27205");

        store.TryGet("D:\\", out DiscOnboardingSession? updated);
        updated!.State.Should().Be(DiscOnboardingState.Complete);
    }

    [Fact]
    public async Task OnMediaFilesScanned_MovieIdDoesNotMatchConfirmedTmdbId_SessionStaysRipping()
    {
        Ulid libraryId = Ulid.NewUlid();
        DiscOnboardingSessionStore store = new();
        store.Set(
            DiscOnboardingSession
                .Create("D:\\")
                .WithJob("job-1", libraryId, confirmedTmdbId: 27205, confirmedMediaType: "movie")
        );

        Mock<IEventBus> eventBus = SubscribableEventBus();

        using DiscOnboardingCompletionEventHandler handler = new(
            eventBus.Object,
            store,
            ContextFactory
        );

        // A different movie finished importing in the same library — must not
        // be mistaken for this session's rip.
        await handler.OnMediaFilesScanned(
            new() { MediaId = 999, LibraryId = libraryId },
            CancellationToken.None
        );

        eventBus.Verify(
            b =>
                b.PublishAsync(
                    It.IsAny<DiscOnboardingStateChangedEvent>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
        store.TryGet("D:\\", out DiscOnboardingSession? unchanged);
        unchanged!.State.Should().Be(DiscOnboardingState.Ripping);
    }

    [Fact]
    public async Task OnMediaFilesScanned_TvEpisodeMatchesConfirmedShow_CompletesWithEpisodeId()
    {
        Ulid libraryId = Ulid.NewUlid();

        await using (MediaContext seed = new(_dbOptions))
        {
            seed.Libraries.Add(
                new Library
                {
                    Id = libraryId,
                    Title = "TV Shows",
                    Type = "tv",
                    Order = 1,
                }
            );
            seed.Tvs.Add(
                new Tv
                {
                    Id = 1399,
                    Title = "Game of Thrones",
                    LibraryId = libraryId,
                }
            );
            seed.Seasons.Add(
                new Season
                {
                    Id = 3624,
                    TvId = 1399,
                    SeasonNumber = 1,
                }
            );
            seed.Episodes.Add(
                new Episode
                {
                    Id = 555,
                    TvId = 1399,
                    SeasonId = 3624,
                    Title = "Winter Is Coming",
                    SeasonNumber = 1,
                    EpisodeNumber = 1,
                }
            );
            await seed.SaveChangesAsync();
        }

        DiscOnboardingSessionStore store = new();
        store.Set(
            DiscOnboardingSession
                .Create("D:\\")
                .WithJob("job-1", libraryId, confirmedTmdbId: 1399, confirmedMediaType: "tv")
        );

        Mock<IEventBus> eventBus = SubscribableEventBus();
        DiscOnboardingStateChangedEvent? published = null;
        eventBus
            .Setup(b =>
                b.PublishAsync(
                    It.IsAny<DiscOnboardingStateChangedEvent>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<DiscOnboardingStateChangedEvent, CancellationToken>(
                (evt, _) => published = evt
            )
            .Returns(Task.CompletedTask);

        using DiscOnboardingCompletionEventHandler handler = new(
            eventBus.Object,
            store,
            ContextFactory
        );

        await handler.OnMediaFilesScanned(
            new() { MediaId = 555, LibraryId = libraryId },
            CancellationToken.None
        );

        published.Should().NotBeNull();
        published!.StateData.ResultType.Should().Be("tv");
        published.StateData.ResultId.Should().Be("555");
    }

    [Fact]
    public async Task OnMediaFilesScanned_DifferentLibrary_IsIgnored()
    {
        DiscOnboardingSessionStore store = new();
        store.Set(
            DiscOnboardingSession
                .Create("D:\\")
                .WithJob(
                    "job-1",
                    Ulid.NewUlid(),
                    confirmedTmdbId: 27205,
                    confirmedMediaType: "movie"
                )
        );

        Mock<IEventBus> eventBus = SubscribableEventBus();

        using DiscOnboardingCompletionEventHandler handler = new(
            eventBus.Object,
            store,
            ContextFactory
        );

        await handler.OnMediaFilesScanned(
            new() { MediaId = 27205, LibraryId = Ulid.NewUlid() },
            CancellationToken.None
        );

        eventBus.Verify(
            b =>
                b.PublishAsync(
                    It.IsAny<DiscOnboardingStateChangedEvent>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    private sealed class TestDbContextFactory(DbContextOptions<MediaContext> options)
        : IDbContextFactory<MediaContext>
    {
        public MediaContext CreateDbContext() => new(options);
    }
}

file static class MockExtensions
{
    public static Mock<T> Also<T>(this Mock<T> mock, Action<Mock<T>> configure)
        where T : class
    {
        configure(mock);
        return mock;
    }
}
