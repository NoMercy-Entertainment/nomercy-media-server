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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.Encoder.Composition;
using NoMercy.Events;
using NoMercy.Events.Encoding;
using NoMercy.Events.Library;
using NoMercy.MediaProcessing.EventHandlers;
using NoMercy.MediaProcessing.Jobs.SegmentJobs;
using NoMercy.MediaProcessing.Segments;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.Tests.MediaProcessing.Segments;

/// <summary>
/// When the analysis runs: after an encode (that item's season or movie) and as
/// a backfill after a library scan (only what was never analysed).
/// </summary>
[Trait("Category", "Unit")]
public class SegmentAnalysisSchedulingTests
{
    private readonly Ulid _libraryId = Ulid.NewUlid();
    private readonly InMemoryMediaFactory _factory = new();
    private readonly List<SegmentAnalysisJob> _queued = [];
    private readonly Mock<IJobDispatcher> _dispatcher = new();
    private readonly Mock<ISegmentAnalysisState> _state = new();
    private readonly Mock<ISegmentAnalysisService> _analysis = new();

    public SegmentAnalysisSchedulingTests()
    {
        _dispatcher
            .Setup(d => d.Dispatch(It.IsAny<IShouldQueue>(), It.IsAny<string>(), It.IsAny<int>()))
            .Callback<IShouldQueue, string, int>((job, _, _) => _queued.Add((SegmentAnalysisJob)job));
        _state
            .Setup(s => s.AnalysedAsync(It.IsAny<SegmentAnalysisTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => []);
        _state
            .Setup(s => s.GivenUpAsync(It.IsAny<SegmentAnalysisTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => []);

        using MediaContext context = _factory.CreateDbContext();
        context.Tvs.Add(new Tv { Id = 1, Title = "Hellsing" });
        context.LibraryTv.Add(new LibraryTv { LibraryId = _libraryId, TvId = 1 });
        context.LibraryMovie.Add(new LibraryMovie { LibraryId = _libraryId, MovieId = 129 });
        context.Seasons.AddRange(new Season { Id = 10, TvId = 1, SeasonNumber = 1 }, new Season { Id = 11, TvId = 1, SeasonNumber = 2 });
        for (int id = 1; id <= 3; id++)
        {
            context.Episodes.Add(new Episode { Id = id, TvId = 1, SeasonId = 10, SeasonNumber = 1, EpisodeNumber = id });
            context.VideoFiles.Add(new VideoFile { EpisodeId = id, HostFolder = "/show", Filename = $"/e{id}.m3u8" });
        }

        // Season 11 is not encoded yet.
        context.Episodes.Add(new Episode { Id = 4, TvId = 1, SeasonId = 11, SeasonNumber = 2, EpisodeNumber = 1 });
        context.Movies.Add(new Movie { Id = 129, Title = "Movie" });
        context.VideoFiles.Add(new VideoFile { MovieId = 129, HostFolder = "/movie", Filename = "/movie.m3u8" });
        context.SaveChanges();
    }

    [Fact]
    public async Task AfterAnEncode_TheEpisodesSeasonIsQueued()
    {
        await Scheduler().OnEncodingCompletedAsync(Completed(2), CancellationToken.None);

        _queued.Should().ContainSingle().Which.SeasonId.Should().Be(10);
    }

    [Fact]
    public async Task AfterAMovieEncode_TheMovieIsQueued()
    {
        await Scheduler().OnEncodingCompletedAsync(Completed(129), CancellationToken.None);

        _queued.Should().ContainSingle().Which.MovieId.Should().Be(129);
    }

    [Fact]
    public async Task LibraryScan_QueuesEveryEncodedSeasonAndMovie_NeverAnalysedBefore()
    {
        await Scheduler().OnLibraryScanCompletedAsync(Scanned(), CancellationToken.None);

        _queued.Select(j => j.SeasonId).OfType<int>().Should().Equal(10);
        _queued.Select(j => j.MovieId).OfType<int>().Should().Equal(129);
        _queued.Select(j => j.Title).Should().Equal("Hellsing, season 1", "Movie");
    }

    [Fact]
    public async Task LibraryScan_SkipsWhatWasAnalysedAlready()
    {
        _state
            .Setup(s => s.AnalysedAsync(SegmentAnalysisTarget.Season, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => [10]);
        _state
            .Setup(s => s.AnalysedAsync(SegmentAnalysisTarget.Movie, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => [129]);

        await Scheduler().OnLibraryScanCompletedAsync(Scanned(), CancellationToken.None);

        _queued.Should().BeEmpty();
    }

    [Fact]
    public async Task LibraryScan_SkipsWhatKeptFailing()
    {
        _state
            .Setup(s => s.GivenUpAsync(SegmentAnalysisTarget.Season, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => [10]);

        await Scheduler().OnLibraryScanCompletedAsync(Scanned(), CancellationToken.None);

        _queued.Select(j => j.SeasonId).OfType<int>().Should().BeEmpty();
        _queued.Select(j => j.MovieId).OfType<int>().Should().Equal(129);
    }

    [Fact]
    public async Task ANewEncode_ForgetsTheSeasonsFailures_AndQueuesIt()
    {
        await Scheduler().OnEncodingCompletedAsync(Completed(2), CancellationToken.None);

        _state.Verify(s => s.ResetFailuresAsync(SegmentAnalysisTarget.Season, 10, It.IsAny<CancellationToken>()));
        _queued.Should().ContainSingle().Which.SeasonId.Should().Be(10);
    }

    [Fact]
    public async Task SwitchedOff_SubscribesToNothing()
    {
        InMemoryEventBus bus = new();
        SegmentAnalysisScheduler scheduler = Scheduler(bus, new EncoderOptions { EnableIntroDetectSubscriber = false });

        await scheduler.StartAsync(CancellationToken.None);
        await bus.PublishAsync(Completed(2));

        _queued.Should().BeEmpty();
    }

    [Fact]
    public async Task Job_AnalysesTheSeason_ThenMarksIt()
    {
        SegmentAnalysisJob job = Job(SegmentAnalysisOutcome.Analysed);

        await job.Handle();

        _analysis.Verify(a => a.AnalyseSeasonAsync(10, It.IsAny<CancellationToken>()), Times.Once);
        _state.Verify(s => s.MarkAnalysedAsync(SegmentAnalysisTarget.Season, 10, It.IsAny<CancellationToken>()), Times.Once);
        job.QueueName.Should().Be("encoder", "the encoder queue holds off during playback");
        job.Priority.Should().BeLessThan(2, "encodes and subtitle OCR go first");
    }

    [Fact]
    public async Task Job_WithNothingToRead_NeitherMarksNorCountsAFailure()
    {
        // An unmounted NAS or a non-local folder: the next scan tries again.
        await Job(SegmentAnalysisOutcome.NoSources).Handle();

        _state.Verify(s => s.MarkAnalysedAsync(It.IsAny<SegmentAnalysisTarget>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _state.Verify(s => s.RecordFailureAsync(It.IsAny<SegmentAnalysisTarget>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Job_ThatMeasuredNothing_CountsAFailure_AndDoesNotMark()
    {
        // FFmpeg missing or without chromaprint: every episode's audio pass failed.
        await Job(SegmentAnalysisOutcome.NothingMeasured).Handle();

        _state.Verify(s => s.RecordFailureAsync(SegmentAnalysisTarget.Season, 10, It.IsAny<CancellationToken>()), Times.Once);
        _state.Verify(s => s.MarkAnalysedAsync(It.IsAny<SegmentAnalysisTarget>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Job_ThatThrows_CountsAFailure_AndDoesNotRetryTheExpensivePasses()
    {
        _analysis
            .Setup(a => a.AnalyseSeasonAsync(10, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("disk gone"));
        SegmentAnalysisJob job = Job(SegmentAnalysisOutcome.Analysed, setUp: false);

        Func<Task> handle = job.Handle;

        await handle.Should().NotThrowAsync("a queue retry would run every pass again");
        _state.Verify(s => s.RecordFailureAsync(SegmentAnalysisTarget.Season, 10, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Job_PassesTheShutdownToken()
    {
        using CancellationTokenSource stopping = new();
        Mock<IHostApplicationLifetime> lifetime = new();
        lifetime.Setup(l => l.ApplicationStopping).Returns(stopping.Token);
        CancellationToken seen = default;
        _analysis
            .Setup(a => a.AnalyseMovieAsync(129, It.IsAny<CancellationToken>()))
            .Callback<int, CancellationToken>((_, ct) => seen = ct)
            .ReturnsAsync(SegmentAnalysisOutcome.Analysed);
        SegmentAnalysisJob job = SegmentAnalysisJob.ForMovie(129);
        job.InjectStorageServices(
            new ServiceCollection()
                .AddSingleton(_analysis.Object)
                .AddSingleton(_state.Object)
                .AddSingleton(lifetime.Object)
                .BuildServiceProvider()
        );

        await job.Handle();

        seen.Should().Be(stopping.Token);
    }

    private SegmentAnalysisJob Job(SegmentAnalysisOutcome outcome, bool setUp = true)
    {
        if (setUp)
        {
            _analysis
                .Setup(a => a.AnalyseSeasonAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(outcome);
        }

        SegmentAnalysisJob job = SegmentAnalysisJob.ForSeason(10);
        job.InjectStorageServices(
            new ServiceCollection().AddSingleton(_analysis.Object).AddSingleton(_state.Object).BuildServiceProvider()
        );
        return job;
    }

    private SegmentAnalysisScheduler Scheduler(IEventBus? bus = null, EncoderOptions? options = null) =>
        new(
            bus ?? new InMemoryEventBus(),
            _factory,
            _dispatcher.Object,
            _state.Object,
            options ?? new EncoderOptions(),
            NullLogger<SegmentAnalysisScheduler>.Instance
        );

    private static EncodingCompletedEvent Completed(int id) =>
        new() { JobId = id, OutputPath = string.Empty, Duration = TimeSpan.Zero };

    private LibraryScanCompletedEvent Scanned() =>
        new() { LibraryId = _libraryId, LibraryName = "Anime", ItemsFound = 4, Duration = TimeSpan.Zero };

    private sealed class InMemoryMediaFactory : IDbContextFactory<MediaContext>
    {
        private readonly DbContextOptions<MediaContext> _options = new DbContextOptionsBuilder<MediaContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        public MediaContext CreateDbContext() => new(_options);
    }
}
