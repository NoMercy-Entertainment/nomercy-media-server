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
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.ContentAnalysis.Fingerprinting;
using NoMercy.Encoder.Subscribers;
using NoMercy.Events;
using NoMercy.Events.Library;
using NoMercy.Storage;

namespace NoMercy.Tests.Encoder.Subscribers;

public class IntroDetectSubscriberTests
{
    private static IDbContextFactory<MediaContext> InMemoryFactory(string tag)
    {
        DbContextOptions<MediaContext> options = new DbContextOptionsBuilder<MediaContext>()
            .UseInMemoryDatabase($"intro-detect-{tag}-{Ulid.NewUlid()}")
            .Options;
        Mock<IDbContextFactory<MediaContext>> mock = new();
        mock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(options));
        mock.Setup(f => f.CreateDbContext()).Returns(() => new(options));
        return mock.Object;
    }

    private static IntroDetectSubscriber BuildSubscriber(
        IEventBus bus,
        IDbContextFactory<MediaContext> factory,
        IAudioFingerprinter fingerprinter,
        IIntroDetector detector,
        bool enabled = true
    )
    {
        EncoderOptions options = new() { EnableIntroDetectSubscriber = enabled };
        Mock<IStorage> storage = new();
        storage.Setup(s => s.Exists(It.IsAny<string>())).Returns(true);
        return new(
            bus,
            fingerprinter,
            detector,
            options,
            NullLogger<IntroDetectSubscriber>.Instance,
            storage.Object,
            factory
        );
    }

    private static async Task SeedTvSeasonAsync(
        IDbContextFactory<MediaContext> factory,
        Ulid libraryId,
        int tvId,
        int seasonId,
        int[] episodeIds,
        string hostFolder = "/media/tv/"
    )
    {
        await using MediaContext ctx = await factory.CreateDbContextAsync();

        ctx.Libraries.Add(
            new()
            {
                Id = libraryId,
                Title = "TV Library",
                Type = "tv",
            }
        );

        ctx.Tvs.Add(
            new()
            {
                Id = tvId,
                Title = "Test Show",
                TitleSort = "testshow",
                LibraryId = libraryId,
            }
        );

        ctx.Seasons.Add(
            new()
            {
                Id = seasonId,
                TvId = tvId,
                SeasonNumber = 1,
                EpisodeCount = episodeIds.Length,
            }
        );

        ctx.LibraryTv.Add(new(libraryId, tvId));

        for (int index = 0; index < episodeIds.Length; index++)
        {
            int episodeId = episodeIds[index];
            ctx.Episodes.Add(
                new()
                {
                    Id = episodeId,
                    TvId = tvId,
                    SeasonId = seasonId,
                    SeasonNumber = 1,
                    EpisodeNumber = index + 1,
                }
            );
            ctx.VideoFiles.Add(
                new()
                {
                    Filename = $"s01e0{index + 1}.mkv",
                    HostFolder = hostFolder,
                    Folder = hostFolder,
                    EpisodeId = episodeId,
                }
            );
        }

        await ctx.SaveChangesAsync();
    }

    private static AudioFingerprint FakeFingerprint(int frameCount = 100) =>
        new(
            Hashes: [.. Enumerable.Range(0, frameCount).Select(i => (uint)(i * 17))],
            FrameDuration: TimeSpan.FromMilliseconds(125),
            StartTime: TimeSpan.Zero
        );

    [Fact]
    public async Task OnLibraryScanCompleted_DisabledViaOptions_WritesNoContentSegments()
    {
        IDbContextFactory<MediaContext> factory = InMemoryFactory("disabled");
        Mock<IAudioFingerprinter> fingerprinter = new();
        Mock<IIntroDetector> detector = new();
        InMemoryEventBus bus = new();

        using IntroDetectSubscriber subject = BuildSubscriber(
            bus,
            factory,
            fingerprinter.Object,
            detector.Object,
            enabled: false
        );

        Ulid libraryId = Ulid.NewUlid();
        int tvId = 1;
        int seasonId = 10;
        await SeedTvSeasonAsync(factory, libraryId, tvId, seasonId, episodeIds: [100, 101, 102]);

        await bus.PublishAsync(
            new LibraryScanCompletedEvent
            {
                LibraryId = libraryId,
                LibraryName = "TV",
                ItemsFound = 3,
                Duration = TimeSpan.FromSeconds(1),
            }
        );

        await using MediaContext ctx = await factory.CreateDbContextAsync();
        ctx.ContentSegments.Should().BeEmpty("opt-out disables the subscriber");

        fingerprinter.Verify(
            f =>
                f.FingerprintAsync(
                    It.IsAny<string>(),
                    It.IsAny<FingerprintWindow?>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task OnLibraryScanCompleted_NoMatchingSeasons_WritesNoSegments()
    {
        IDbContextFactory<MediaContext> factory = InMemoryFactory("noseasons");
        Mock<IAudioFingerprinter> fingerprinter = new();
        Mock<IIntroDetector> detector = new();
        InMemoryEventBus bus = new();

        using IntroDetectSubscriber subject = BuildSubscriber(
            bus,
            factory,
            fingerprinter.Object,
            detector.Object
        );

        Ulid unrelatedLibrary = Ulid.NewUlid();

        await bus.PublishAsync(
            new LibraryScanCompletedEvent
            {
                LibraryId = unrelatedLibrary,
                LibraryName = "Empty",
                ItemsFound = 0,
                Duration = TimeSpan.Zero,
            }
        );

        await using MediaContext ctx = await factory.CreateDbContextAsync();
        ctx.ContentSegments.Should().BeEmpty();
        fingerprinter.Verify(
            f =>
                f.FingerprintAsync(
                    It.IsAny<string>(),
                    It.IsAny<FingerprintWindow?>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task OnLibraryScanCompleted_MusicLibrary_DoesNoWork_DoesNotFingerprint()
    {
        IDbContextFactory<MediaContext> factory = InMemoryFactory("music");
        Mock<IAudioFingerprinter> fingerprinter = new();
        Mock<IIntroDetector> detector = new();
        InMemoryEventBus bus = new();

        using IntroDetectSubscriber subject = BuildSubscriber(
            bus,
            factory,
            fingerprinter.Object,
            detector.Object
        );

        Ulid musicLibraryId = Ulid.NewUlid();
        await using (MediaContext seedCtx = await factory.CreateDbContextAsync())
        {
            seedCtx.Libraries.Add(
                new()
                {
                    Id = musicLibraryId,
                    Title = "Music",
                    Type = "music",
                }
            );
            await seedCtx.SaveChangesAsync();
        }

        await bus.PublishAsync(
            new LibraryScanCompletedEvent
            {
                LibraryId = musicLibraryId,
                LibraryName = "Music",
                ItemsFound = 0,
                Duration = TimeSpan.Zero,
            }
        );

        await using MediaContext ctx = await factory.CreateDbContextAsync();
        ctx.ContentSegments.Should()
            .BeEmpty("intro detection is a TV feature and must not run on a music library");
        fingerprinter.Verify(
            f =>
                f.FingerprintAsync(
                    It.IsAny<string>(),
                    It.IsAny<FingerprintWindow?>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task OnLibraryScanCompleted_SeasonHasOnlyOneEpisode_SkipsDetection_WritesNoSegments()
    {
        IDbContextFactory<MediaContext> factory = InMemoryFactory("singleepisode");
        Mock<IAudioFingerprinter> fingerprinter = new();
        Mock<IIntroDetector> detector = new();
        InMemoryEventBus bus = new();

        using IntroDetectSubscriber subject = BuildSubscriber(
            bus,
            factory,
            fingerprinter.Object,
            detector.Object
        );

        Ulid libraryId = Ulid.NewUlid();
        await SeedTvSeasonAsync(factory, libraryId, tvId: 2, seasonId: 20, episodeIds: [200]);

        await bus.PublishAsync(
            new LibraryScanCompletedEvent
            {
                LibraryId = libraryId,
                LibraryName = "TV",
                ItemsFound = 1,
                Duration = TimeSpan.Zero,
            }
        );

        await using MediaContext ctx = await factory.CreateDbContextAsync();
        ctx.ContentSegments.Should()
            .BeEmpty("fewer than 2 episodes → cannot detect shared segments");
    }

    [Fact]
    public async Task OnLibraryScanCompleted_TwoEpisodes_WithDetectedIntro_WritesIntroSegmentRows()
    {
        IDbContextFactory<MediaContext> factory = InMemoryFactory("introseeds");
        Mock<IAudioFingerprinter> fingerprinter = new();
        Mock<IIntroDetector> detector = new();
        Mock<IStorage> storage = new();
        storage.Setup(s => s.Exists(It.IsAny<string>())).Returns(true);
        InMemoryEventBus bus = new();

        AudioFingerprint fp = FakeFingerprint();
        fingerprinter
            .Setup(f =>
                f.FingerprintAsync(
                    It.IsAny<string>(),
                    It.IsAny<FingerprintWindow?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(fp);

        IntroMarker introMarker = new(
            Start: TimeSpan.FromSeconds(10),
            End: TimeSpan.FromSeconds(95),
            Confidence: 0.91
        );
        detector
            .Setup(d => d.DetectIntro(It.IsAny<IReadOnlyList<AudioFingerprint>>()))
            .Returns(introMarker);
        detector
            .Setup(d => d.DetectOutro(It.IsAny<IReadOnlyList<AudioFingerprint>>()))
            .Returns((IntroMarker?)null);

        EncoderOptions options = new() { EnableIntroDetectSubscriber = true };
        using IntroDetectSubscriber subject = new(
            bus,
            fingerprinter.Object,
            detector.Object,
            options,
            NullLogger<IntroDetectSubscriber>.Instance,
            storage.Object,
            factory
        );

        Ulid libraryId = Ulid.NewUlid();
        await SeedTvSeasonAsync(factory, libraryId, tvId: 3, seasonId: 30, episodeIds: [300, 301]);

        await bus.PublishAsync(
            new LibraryScanCompletedEvent
            {
                LibraryId = libraryId,
                LibraryName = "TV",
                ItemsFound = 2,
                Duration = TimeSpan.FromSeconds(2),
            }
        );

        await using MediaContext ctx = await factory.CreateDbContextAsync();
        List<ContentSegment> segments = [.. ctx.ContentSegments];

        segments.Should().HaveCount(2, "one intro segment per episode");
        segments
            .Should()
            .AllSatisfy(s =>
            {
                s.SegmentType.Should().Be(ContentSegmentType.Intro);
                s.StartSeconds.Should().BeApproximately(10.0, 0.001);
                s.EndSeconds.Should().BeApproximately(95.0, 0.001);
                s.Confidence.Should().BeApproximately(0.91, 0.001);
                s.Source.Should().Be("detector");
                s.EpisodeId.Should().NotBeNull();
            });
    }

    [Fact]
    public async Task OnLibraryScanCompleted_TwoEpisodes_WithDetectedOutro_WritesOutroSegmentRows()
    {
        IDbContextFactory<MediaContext> factory = InMemoryFactory("outroseeds");
        Mock<IAudioFingerprinter> fingerprinter = new();
        Mock<IIntroDetector> detector = new();
        Mock<IStorage> storage = new();
        storage.Setup(s => s.Exists(It.IsAny<string>())).Returns(true);
        InMemoryEventBus bus = new();

        AudioFingerprint fp = FakeFingerprint();
        fingerprinter
            .Setup(f =>
                f.FingerprintAsync(
                    It.IsAny<string>(),
                    It.IsAny<FingerprintWindow?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(fp);

        detector
            .Setup(d => d.DetectIntro(It.IsAny<IReadOnlyList<AudioFingerprint>>()))
            .Returns((IntroMarker?)null);

        IntroMarker outroMarker = new(
            Start: TimeSpan.FromSeconds(1200),
            End: TimeSpan.FromSeconds(1380),
            Confidence: 0.85
        );
        detector
            .Setup(d => d.DetectOutro(It.IsAny<IReadOnlyList<AudioFingerprint>>()))
            .Returns(outroMarker);

        EncoderOptions options = new() { EnableIntroDetectSubscriber = true };
        using IntroDetectSubscriber subject = new(
            bus,
            fingerprinter.Object,
            detector.Object,
            options,
            NullLogger<IntroDetectSubscriber>.Instance,
            storage.Object,
            factory
        );

        Ulid libraryId = Ulid.NewUlid();
        await SeedTvSeasonAsync(factory, libraryId, tvId: 4, seasonId: 40, episodeIds: [400, 401]);

        await bus.PublishAsync(
            new LibraryScanCompletedEvent
            {
                LibraryId = libraryId,
                LibraryName = "TV",
                ItemsFound = 2,
                Duration = TimeSpan.FromSeconds(2),
            }
        );

        await using MediaContext ctx = await factory.CreateDbContextAsync();
        List<ContentSegment> segments = [.. ctx.ContentSegments];

        segments.Should().HaveCount(2, "one outro segment per episode");
        segments
            .Should()
            .AllSatisfy(s =>
            {
                s.SegmentType.Should().Be(ContentSegmentType.Outro);
                s.StartSeconds.Should().BeApproximately(1200.0, 0.001);
                s.EndSeconds.Should().BeApproximately(1380.0, 0.001);
                s.Source.Should().Be("detector");
            });
    }

    [Fact]
    public async Task OnLibraryScanCompleted_EpisodeWithManualIntro_IsSkipped_OtherEpisodeGetsDetectorSegment()
    {
        IDbContextFactory<MediaContext> factory = InMemoryFactory("manualskip");
        Mock<IAudioFingerprinter> fingerprinter = new();
        Mock<IIntroDetector> detector = new();
        Mock<IStorage> storage = new();
        storage.Setup(s => s.Exists(It.IsAny<string>())).Returns(true);
        InMemoryEventBus bus = new();

        AudioFingerprint fp = FakeFingerprint();
        fingerprinter
            .Setup(f =>
                f.FingerprintAsync(
                    It.IsAny<string>(),
                    It.IsAny<FingerprintWindow?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(fp);

        IntroMarker introMarker = new(
            Start: TimeSpan.FromSeconds(5),
            End: TimeSpan.FromSeconds(90),
            Confidence: 0.95
        );
        detector
            .Setup(d => d.DetectIntro(It.IsAny<IReadOnlyList<AudioFingerprint>>()))
            .Returns(introMarker);
        detector
            .Setup(d => d.DetectOutro(It.IsAny<IReadOnlyList<AudioFingerprint>>()))
            .Returns((IntroMarker?)null);

        EncoderOptions options = new() { EnableIntroDetectSubscriber = true };
        using IntroDetectSubscriber subject = new(
            bus,
            fingerprinter.Object,
            detector.Object,
            options,
            NullLogger<IntroDetectSubscriber>.Instance,
            storage.Object,
            factory
        );

        Ulid libraryId = Ulid.NewUlid();
        int episodeWithManual = 500;
        int episodeWithoutManual = 501;
        await SeedTvSeasonAsync(
            factory,
            libraryId,
            tvId: 5,
            seasonId: 50,
            episodeIds: [episodeWithManual, episodeWithoutManual]
        );

        await using (MediaContext seedCtx = await factory.CreateDbContextAsync())
        {
            seedCtx.ContentSegments.Add(
                new()
                {
                    EpisodeId = episodeWithManual,
                    SegmentType = ContentSegmentType.Intro,
                    StartSeconds = 8,
                    EndSeconds = 85,
                    Source = "manual",
                    Confidence = 1.0,
                }
            );
            await seedCtx.SaveChangesAsync();
        }

        await bus.PublishAsync(
            new LibraryScanCompletedEvent
            {
                LibraryId = libraryId,
                LibraryName = "TV",
                ItemsFound = 2,
                Duration = TimeSpan.FromSeconds(1),
            }
        );

        await using MediaContext ctx = await factory.CreateDbContextAsync();
        List<ContentSegment> segments = [.. ctx.ContentSegments];

        ContentSegment detectorSegment = segments
            .Where(s => s.Source == "detector")
            .Should()
            .ContainSingle("only the episode without manual coverage gets a detector segment")
            .Which;

        detectorSegment.EpisodeId.Should().Be(episodeWithoutManual);
        detectorSegment.SegmentType.Should().Be(ContentSegmentType.Intro);

        segments
            .Where(s => s.Source == "manual" && s.EpisodeId == episodeWithManual)
            .Should()
            .ContainSingle("manual segment is untouched");
    }

    [Fact]
    public async Task OnLibraryScanCompleted_FingerprintThrows_DoesNotPropagate_WritesNoSegments()
    {
        IDbContextFactory<MediaContext> factory = InMemoryFactory("fpthrows");
        Mock<IAudioFingerprinter> fingerprinter = new();
        Mock<IIntroDetector> detector = new();
        Mock<IStorage> storage = new();
        storage.Setup(s => s.Exists(It.IsAny<string>())).Returns(true);
        InMemoryEventBus bus = new();

        fingerprinter
            .Setup(f =>
                f.FingerprintAsync(
                    It.IsAny<string>(),
                    It.IsAny<FingerprintWindow?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new InvalidOperationException("chromaprint unavailable"));

        EncoderOptions options = new() { EnableIntroDetectSubscriber = true };
        using IntroDetectSubscriber subject = new(
            bus,
            fingerprinter.Object,
            detector.Object,
            options,
            NullLogger<IntroDetectSubscriber>.Instance,
            storage.Object,
            factory
        );

        Ulid libraryId = Ulid.NewUlid();
        await SeedTvSeasonAsync(factory, libraryId, tvId: 6, seasonId: 60, episodeIds: [600, 601]);

        Func<Task> act = () =>
            bus.PublishAsync(
                new LibraryScanCompletedEvent
                {
                    LibraryId = libraryId,
                    LibraryName = "TV",
                    ItemsFound = 2,
                    Duration = TimeSpan.Zero,
                }
            );

        await act.Should().NotThrowAsync("fingerprint failures are caught-and-logged per episode");

        await using MediaContext ctx = await factory.CreateDbContextAsync();
        ctx.ContentSegments.Should()
            .BeEmpty("fingerprinting failed for all episodes so no segments can be written");
    }

    [Fact]
    public void Constructor_SubscribesToLibraryScanCompletedEvent()
    {
        Mock<IEventBus> bus = new();
        Mock<IDisposable> stubSub = new();
        bus.Setup(b =>
                b.Subscribe<LibraryScanCompletedEvent>(
                    It.IsAny<Func<LibraryScanCompletedEvent, CancellationToken, Task>>()
                )
            )
            .Returns(stubSub.Object);
        Mock<IStorage> storage = new();
        EncoderOptions options = new() { EnableIntroDetectSubscriber = true };

        using IntroDetectSubscriber _ = new(
            bus.Object,
            Mock.Of<IAudioFingerprinter>(),
            Mock.Of<IIntroDetector>(),
            options,
            NullLogger<IntroDetectSubscriber>.Instance,
            storage.Object,
            InMemoryFactory("ctor")
        );

        bus.Verify(
            b =>
                b.Subscribe<LibraryScanCompletedEvent>(
                    It.IsAny<Func<LibraryScanCompletedEvent, CancellationToken, Task>>()
                ),
            Times.Once
        );
    }

    [Fact]
    public void Dispose_ReleasesSubscription()
    {
        Mock<IDisposable> subscription = new();
        Mock<IEventBus> bus = new();
        bus.Setup(b =>
                b.Subscribe<LibraryScanCompletedEvent>(
                    It.IsAny<Func<LibraryScanCompletedEvent, CancellationToken, Task>>()
                )
            )
            .Returns(subscription.Object);

        IntroDetectSubscriber subject = new(
            bus.Object,
            Mock.Of<IAudioFingerprinter>(),
            Mock.Of<IIntroDetector>(),
            new(),
            NullLogger<IntroDetectSubscriber>.Instance,
            Mock.Of<IStorage>(),
            InMemoryFactory("dispose")
        );

        subject.Dispose();

        subscription.Verify(s => s.Dispose(), Times.Once);
    }
}
