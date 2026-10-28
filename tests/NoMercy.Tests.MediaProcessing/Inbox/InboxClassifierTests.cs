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

using Moq;
using NoMercy.Database.Models.Libraries;
using NoMercy.MediaProcessing.Inbox;
using NoMercy.MediaProcessing.Shows;

namespace NoMercy.Tests.MediaProcessing.Inbox;

[Trait("Category", "Unit")]
public partial class InboxClassifierTests
{
    [System.Text.RegularExpressions.GeneratedRegex(
        @"\s*\((?:19|20)\d{2}\)\s*",
        System.Text.RegularExpressions.RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial System.Text.RegularExpressions.Regex ParenthesizedYearWithSpaces();

    [System.Text.RegularExpressions.GeneratedRegex(
        @"\((?:19|20)(\d{2})\)",
        System.Text.RegularExpressions.RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial System.Text.RegularExpressions.Regex ParenthesizedYear();

    // -----------------------------------------------------------------------
    // Factory helpers
    // -----------------------------------------------------------------------

    private static InboxClassifier MakeClassifier(
        IInboxMetadataProbe? probe = null,
        IInboxAudioTagReader? tagReader = null,
        IMediaTypeClassifier? mediaTypeClassifier = null
    )
    {
        probe ??= new Mock<IInboxMetadataProbe>().Object;
        tagReader ??= new Mock<IInboxAudioTagReader>().Object;
        mediaTypeClassifier ??= NonAnimeClassifier().Object;
        return new(probe, tagReader, mediaTypeClassifier);
    }

    private static Mock<IMediaTypeClassifier> NonAnimeClassifier()
    {
        Mock<IMediaTypeClassifier> mock = new();
        mock.Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<int?>())).ReturnsAsync("tv");
        return mock;
    }

    private static Mock<IMediaTypeClassifier> AnimeClassifier()
    {
        Mock<IMediaTypeClassifier> mock = new();
        mock.Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<int?>()))
            .ReturnsAsync("anime");
        return mock;
    }

    private static Mock<IInboxMetadataProbe> EmptyProbe()
    {
        Mock<IInboxMetadataProbe> mock = new();
        mock.Setup(p =>
                p.SearchMoviesAsync(
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([]);
        mock.Setup(p =>
                p.SearchTvAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync([]);
        mock.Setup(p => p.LookupMusicReleaseAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CandidateMatch?)null);
        return mock;
    }

    private static CandidateMatch StrongMovie(string title, int year) =>
        new()
        {
            Provider = "tmdb",
            ExternalId = "123",
            Title = title,
            Year = year,
            Score = 0.85,
        };

    private static CandidateMatch StrongTv(string title, int year) =>
        new()
        {
            Provider = "tmdb",
            ExternalId = "456",
            Title = title,
            Year = year,
            Score = 0.80,
        };

    private static CandidateMatch WeakHit(string title) =>
        new()
        {
            Provider = "tmdb",
            ExternalId = "999",
            Title = title,
            Year = null,
            Score = 0.30,
        };

    private static CandidateMatch MusicCandidate(Guid releaseId, string title) =>
        new()
        {
            Provider = "musicbrainz",
            ExternalId = releaseId.ToString(),
            Title = title,
            Year = 2020,
            Score = 1.0,
        };

    // -----------------------------------------------------------------------
    // MediaFamilyOf — extension split
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("Inbox/song.flac", "music")]
    [InlineData("Inbox/song.mp3", "music")]
    [InlineData("Inbox/song.opus", "music")]
    [InlineData("Inbox/song.wav", "music")]
    [InlineData("Inbox/song.m4a", "music")]
    public void MediaFamilyOf_AudioExtensions_ReturnMusic(string path, string expected)
    {
        InboxClassifier.MediaFamilyOf(path).Should().Be(expected);
    }

    [Theory]
    [InlineData("Inbox/movie.mkv", "video")]
    [InlineData("Inbox/movie.mp4", "video")]
    [InlineData("Inbox/movie.avi", "video")]
    [InlineData("Inbox/movie.webm", "video")]
    [InlineData("Inbox/movie.mov", "video")]
    [InlineData("Inbox/stream.m3u8", "video")]
    public void MediaFamilyOf_VideoExtensions_ReturnVideo(string path, string expected)
    {
        InboxClassifier.MediaFamilyOf(path).Should().Be(expected);
    }

    [Theory]
    [InlineData("Inbox/document.pdf")]
    [InlineData("Inbox/image.jpg")]
    [InlineData("Inbox/archive.zip")]
    public void MediaFamilyOf_UnknownExtensions_ReturnUnknown(string path)
    {
        InboxClassifier.MediaFamilyOf(path).Should().Be("unknown");
    }

    // -----------------------------------------------------------------------
    // StructuralType — video structure parse
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("Inbox/Breaking Bad/Season 01/Breaking Bad S01E01.mkv", "tv")]
    [InlineData("Inbox/The Office 1x01.mkv", "tv")]
    [InlineData("Inbox/Show Name S02E03 720p.mkv", "tv")]
    [InlineData("Inbox/Game of Thrones/Season 1/S01E01.mkv", "tv")]
    [InlineData("Inbox/Series/Season 2/Episode.mkv", "tv")]
    public void StructuralType_TvShapes_ReturnTv(string path, string expected)
    {
        InboxClassifier.StructuralType(path).Should().Be(expected);
    }

    [Theory]
    [InlineData("Inbox/The Matrix (1999)/The Matrix (1999).mkv", "movie")]
    [InlineData("Inbox/Inception (2010).mkv", "movie")]
    [InlineData("Inbox/Interstellar (2014).mkv", "movie")]
    [InlineData("Inbox/The Dark Knight (2008)/The Dark Knight (2008).mkv", "movie")]
    public void StructuralType_MovieShapes_ReturnMovie(string path, string expected)
    {
        InboxClassifier.StructuralType(path).Should().Be(expected);
    }

    // Fansub-bracket filenames are structurally episodic ("tv-shaped"); the
    // anime/tv split itself is decided by IMediaTypeClassifier (Kitsu), not
    // by StructuralType, so normally-named anime with no bracket still
    // classifies correctly.
    [Theory]
    [InlineData("Inbox/[SubsPlease] Frieren - 01 (1080p).mkv", "tv")]
    [InlineData("Inbox/[Erai-raws] Bleach - 366 [1080p].mkv", "tv")]
    [InlineData("Inbox/[HorribleSubs] Attack on Titan - 25 [720p].mkv", "tv")]
    public void StructuralType_FansubShapes_ReturnTv(string path, string expected)
    {
        InboxClassifier.StructuralType(path).Should().Be(expected);
    }

    [Theory]
    [InlineData("Inbox/random-clip.mkv")]
    [InlineData("Inbox/movie.mkv")]
    [InlineData("Inbox/untitled.mp4")]
    public void StructuralType_AmbiguousShapes_ReturnUnknown(string path)
    {
        InboxClassifier.StructuralType(path).Should().Be("unknown");
    }

    // -----------------------------------------------------------------------
    // Classify — video: movie/high when single strong movie hit, no TV hit
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Classify_StrongMovieHit_NoTvHit_ReturnsMovieHigh()
    {
        Mock<IInboxMetadataProbe> probe = new();
        probe
            .Setup(p => p.SearchMoviesAsync("The Matrix", 1999, It.IsAny<CancellationToken>()))
            .ReturnsAsync([StrongMovie("The Matrix", 1999)]);
        probe
            .Setup(p =>
                p.SearchTvAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync([]);

        InboxClassifier classifier = MakeClassifier(probe: probe.Object);
        ClassificationResult result = await classifier.Classify(
            "Inbox/The Matrix (1999)/The Matrix (1999).mkv",
            Ulid.NewUlid(),
            CancellationToken.None
        );

        result.DetectedType.Should().Be("movie");
        result.Confidence.Should().Be("high");
        result.Candidates.Should().NotBeEmpty();
        result.Candidates[0].Provider.Should().Be("tmdb");
    }

    // -----------------------------------------------------------------------
    // Classify — video: low when strong hits on BOTH movie and TV
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Classify_StrongHitsOnBothMovieAndTv_ReturnsLow()
    {
        Mock<IInboxMetadataProbe> probe = new();
        probe
            .Setup(p =>
                p.SearchMoviesAsync(
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([StrongMovie("Inception", 2010)]);
        probe
            .Setup(p =>
                p.SearchTvAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync([StrongTv("Inception", 2010)]);

        InboxClassifier classifier = MakeClassifier(probe: probe.Object);
        ClassificationResult result = await classifier.Classify(
            "Inbox/Inception (2010).mkv",
            Ulid.NewUlid(),
            CancellationToken.None
        );

        result.Confidence.Should().Be("low");
    }

    // -----------------------------------------------------------------------
    // Classify — video: anime structural type stays low when ambiguous
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Classify_AnimeStructureNoProviderHit_ReturnsAnimeLow()
    {
        Mock<IInboxMetadataProbe> probe = EmptyProbe();

        InboxClassifier classifier = MakeClassifier(
            probe: probe.Object,
            mediaTypeClassifier: AnimeClassifier().Object
        );
        ClassificationResult result = await classifier.Classify(
            "Inbox/[SubsPlease] Frieren - 01 (1080p).mkv",
            Ulid.NewUlid(),
            CancellationToken.None
        );

        result.DetectedType.Should().Be("anime");
        result.Confidence.Should().Be("low");
    }

    // The bug this slice fixes: a normally-named anime file with no fansub
    // bracket used to structurally classify as "tv" and stop there. It must
    // now be handed to the Kitsu-backed classifier the same as a bracketed
    // fansub file, and land as "anime" when Kitsu says so.
    [Fact]
    public async Task Classify_NormallyNamedAnimeNoFansubBracket_DelegatesToClassifierReturnsAnime()
    {
        Mock<IInboxMetadataProbe> probe = EmptyProbe();

        InboxClassifier classifier = MakeClassifier(
            probe: probe.Object,
            mediaTypeClassifier: AnimeClassifier().Object
        );
        ClassificationResult result = await classifier.Classify(
            "Inbox/Attack on Titan/Season 01/Attack on Titan S01E01.mkv",
            Ulid.NewUlid(),
            CancellationToken.None
        );

        result.DetectedType.Should().Be("anime");
    }

    // -----------------------------------------------------------------------
    // Classify — music: tags with MusicBrainz release id → music/high
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Classify_MusicTagsWithReleaseId_ReturnsMusicHigh()
    {
        Guid releaseId = Guid.NewGuid();
        CandidateMatch candidate = MusicCandidate(releaseId, "Artist – Album");

        Mock<IInboxAudioTagReader> tagReader = new();
        tagReader
            .Setup(r =>
                r.ReadAsync(It.IsAny<string>(), It.IsAny<Ulid>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(
                new InboxAudioTags
                {
                    MusicBrainzReleaseId = releaseId,
                    Album = "Album",
                    Artist = "Artist",
                }
            );

        Mock<IInboxMetadataProbe> probe = new();
        probe
            .Setup(p => p.LookupMusicReleaseAsync(releaseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(candidate);

        InboxClassifier classifier = MakeClassifier(
            probe: probe.Object,
            tagReader: tagReader.Object
        );
        ClassificationResult result = await classifier.Classify(
            "Inbox/Artist - Album/01 - Track.flac",
            Ulid.NewUlid(),
            CancellationToken.None
        );

        result.DetectedType.Should().Be("music");
        result.Confidence.Should().Be("high");
        result.Candidates.Should().HaveCount(1);
        result.Candidates[0].Provider.Should().Be("musicbrainz");
    }

    // -----------------------------------------------------------------------
    // Classify — music: tags present but no release id → music/medium
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Classify_MusicTagsWithoutReleaseId_ReturnsMusicMedium()
    {
        Mock<IInboxAudioTagReader> tagReader = new();
        tagReader
            .Setup(r =>
                r.ReadAsync(It.IsAny<string>(), It.IsAny<Ulid>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(
                new InboxAudioTags
                {
                    MusicBrainzReleaseId = null,
                    Album = "Some Album",
                    Artist = "Some Artist",
                }
            );

        InboxClassifier classifier = MakeClassifier(tagReader: tagReader.Object);
        ClassificationResult result = await classifier.Classify(
            "Inbox/song.mp3",
            Ulid.NewUlid(),
            CancellationToken.None
        );

        result.DetectedType.Should().Be("music");
        result.Confidence.Should().Be("medium");
    }

    // -----------------------------------------------------------------------
    // Classify — music: no tags → music/low
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Classify_MusicNoTags_ReturnsMusicLow()
    {
        Mock<IInboxAudioTagReader> tagReader = new();
        tagReader
            .Setup(r =>
                r.ReadAsync(It.IsAny<string>(), It.IsAny<Ulid>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync((InboxAudioTags?)null);

        InboxClassifier classifier = MakeClassifier(tagReader: tagReader.Object);
        ClassificationResult result = await classifier.Classify(
            "Inbox/unknown-track.flac",
            Ulid.NewUlid(),
            CancellationToken.None
        );

        result.DetectedType.Should().Be("music");
        result.Confidence.Should().Be("low");
        result.Candidates.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // Task 2.6 — Confidence tuning table
    // Real-world messy naming shapes: each must land at its intended type+confidence.
    // Ambiguous cases must land at low. Network calls are all stubbed.
    // -----------------------------------------------------------------------

    // Clean movie names
    [Theory]
    [InlineData("Inbox/Parasite (2019)/Parasite (2019).mkv", "movie", "high")]
    [InlineData("Inbox/Avengers Endgame (2019).mkv", "movie", "high")]
    [InlineData("Inbox/Blade Runner 2049 (2017)/Blade Runner 2049 (2017).mkv", "movie", "high")]
    public async Task ConfidenceTuning_CleanMovieNames_ReturnMovieHigh(
        string path,
        string expectedType,
        string expectedConfidence
    )
    {
        Mock<IInboxMetadataProbe> probe = new();
        string title = ExtractMovieTitleFromPath(path);
        int? year = ExtractYearFromPath(path);

        probe
            .Setup(p =>
                p.SearchMoviesAsync(
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([StrongMovie(title, year ?? 2019)]);
        probe
            .Setup(p =>
                p.SearchTvAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync([]);

        InboxClassifier classifier = MakeClassifier(probe: probe.Object);
        ClassificationResult result = await classifier.Classify(
            path,
            Ulid.NewUlid(),
            CancellationToken.None
        );

        result.DetectedType.Should().Be(expectedType);
        result.Confidence.Should().Be(expectedConfidence);
    }

    // Clean TV show names
    [Theory]
    [InlineData("Inbox/Breaking Bad/Season 01/Breaking Bad S01E01.mkv", "tv", "high")]
    [InlineData("Inbox/The Office/S02E03.mkv", "tv", "high")]
    [InlineData("Inbox/Succession S03E01 720p.mkv", "tv", "high")]
    public async Task ConfidenceTuning_CleanTvNames_ReturnTvHigh(
        string path,
        string expectedType,
        string expectedConfidence
    )
    {
        Mock<IInboxMetadataProbe> probe = new();
        probe
            .Setup(p =>
                p.SearchTvAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync([StrongTv("Breaking Bad", 2008)]);
        probe
            .Setup(p =>
                p.SearchMoviesAsync(
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([]);

        InboxClassifier classifier = MakeClassifier(probe: probe.Object);
        ClassificationResult result = await classifier.Classify(
            path,
            Ulid.NewUlid(),
            CancellationToken.None
        );

        result.DetectedType.Should().Be(expectedType);
        result.Confidence.Should().Be(expectedConfidence);
    }

    // Multi-episode folder — still TV
    [Fact]
    public async Task ConfidenceTuning_MultiEpisodeFolder_ReturnsTv()
    {
        Mock<IInboxMetadataProbe> probe = new();
        probe
            .Setup(p =>
                p.SearchTvAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync([StrongTv("The Wire", 2002)]);
        probe
            .Setup(p =>
                p.SearchMoviesAsync(
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([]);

        InboxClassifier classifier = MakeClassifier(probe: probe.Object);
        ClassificationResult result = await classifier.Classify(
            "Inbox/The Wire/Season 1/S01E01-S01E03.mkv",
            Ulid.NewUlid(),
            CancellationToken.None
        );

        result.DetectedType.Should().Be("tv");
    }

    // Year in folder only (not filename) — still classifies as movie
    [Fact]
    public async Task ConfidenceTuning_YearInFolderOnly_ClassifiesAsMovie()
    {
        Mock<IInboxMetadataProbe> probe = new();
        probe
            .Setup(p =>
                p.SearchMoviesAsync(
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([StrongMovie("Fight Club", 1999)]);
        probe
            .Setup(p =>
                p.SearchTvAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync([]);

        InboxClassifier classifier = MakeClassifier(probe: probe.Object);
        ClassificationResult result = await classifier.Classify(
            "Inbox/Fight Club (1999)/fight.club.mkv",
            Ulid.NewUlid(),
            CancellationToken.None
        );

        result.DetectedType.Should().Be("movie");
    }

    // Anime fansub variants — all land at anime (conservative: low or medium)
    [Theory]
    [InlineData("Inbox/[Erai-raws] One Piece - 1000 [1080p].mkv", "anime")]
    [InlineData("Inbox/[HorribleSubs] Naruto Shippuden - 500 [720p].mkv", "anime")]
    public async Task ConfidenceTuning_AnimeFansubVariants_ReturnAnime(
        string path,
        string expectedType
    )
    {
        InboxClassifier classifier = MakeClassifier(
            probe: EmptyProbe().Object,
            mediaTypeClassifier: AnimeClassifier().Object
        );
        ClassificationResult result = await classifier.Classify(
            path,
            Ulid.NewUlid(),
            CancellationToken.None
        );

        result.DetectedType.Should().Be(expectedType);
    }

    // Ambiguous — bare filename, no year, no episode tokens → unknown/low
    [Theory]
    [InlineData("Inbox/random-clip.mkv")]
    [InlineData("Inbox/movie.mkv")]
    [InlineData("Inbox/sample.mp4")]
    public async Task ConfidenceTuning_AmbiguousFiles_ReturnUnknownOrLow(string path)
    {
        InboxClassifier classifier = MakeClassifier(probe: EmptyProbe().Object);
        ClassificationResult result = await classifier.Classify(
            path,
            Ulid.NewUlid(),
            CancellationToken.None
        );

        result.Confidence.Should().Be("low");
    }

    // Messy movie name (quality tags in filename) — still resolves to movie/high
    [Fact]
    public async Task ConfidenceTuning_MessyMovieFilename_ResolvesToMovieHigh()
    {
        Mock<IInboxMetadataProbe> probe = new();
        probe
            .Setup(p =>
                p.SearchMoviesAsync(
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([StrongMovie("The Godfather", 1972)]);
        probe
            .Setup(p =>
                p.SearchTvAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync([]);

        InboxClassifier classifier = MakeClassifier(probe: probe.Object);
        ClassificationResult result = await classifier.Classify(
            "Inbox/The Godfather (1972)/The.Godfather.1972.1080p.BluRay.x264.mkv",
            Ulid.NewUlid(),
            CancellationToken.None
        );

        result.DetectedType.Should().Be("movie");
        result.Confidence.Should().Be("high");
    }

    // Weak-hit movie: structural type clear but score below threshold → medium
    [Fact]
    public async Task ConfidenceTuning_WeakMovieHit_ReturnsMedium()
    {
        Mock<IInboxMetadataProbe> probe = new();
        probe
            .Setup(p =>
                p.SearchMoviesAsync(
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([WeakHit("Some Obscure Movie")]);
        probe
            .Setup(p =>
                p.SearchTvAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync([]);

        InboxClassifier classifier = MakeClassifier(probe: probe.Object);
        ClassificationResult result = await classifier.Classify(
            "Inbox/Obscure (2005).mkv",
            Ulid.NewUlid(),
            CancellationToken.None
        );

        result.DetectedType.Should().Be("movie");
        result.Confidence.Should().Be("medium");
    }

    // Year mismatch → medium not high
    [Fact]
    public async Task ConfidenceTuning_YearMismatch_ReturnsMedium()
    {
        // Query year = 2010 but hit year = 1990 — big mismatch → not strong
        Mock<IInboxMetadataProbe> probe = new();
        CandidateMatch yearMismatchHit = new()
        {
            Provider = "tmdb",
            ExternalId = "777",
            Title = "Total Recall",
            Year = 1990,
            Score = 0.80,
        };
        probe
            .Setup(p =>
                p.SearchMoviesAsync(
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([yearMismatchHit]);
        probe
            .Setup(p =>
                p.SearchTvAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync([]);

        InboxClassifier classifier = MakeClassifier(probe: probe.Object);
        ClassificationResult result = await classifier.Classify(
            "Inbox/Total Recall (2012).mkv",
            Ulid.NewUlid(),
            CancellationToken.None
        );

        // Year in path is 2012, hit year is 1990 — mismatch > 1 year → not strong → medium
        result.DetectedType.Should().Be("movie");
        result.Confidence.Should().Be("medium");
    }

    // -----------------------------------------------------------------------
    // No network calls are made for pure static tests (no probe invocation)
    // -----------------------------------------------------------------------

    [Fact]
    public void StructuralType_NeverCallsProbe()
    {
        // This test verifies StructuralType is pure static — just call it
        // without any probe setup and it must not throw.
        string result = InboxClassifier.StructuralType("Inbox/Show S01E01.mkv");
        result.Should().Be("tv");
    }

    // -----------------------------------------------------------------------
    // Private helpers used in test setup (not production code)
    // -----------------------------------------------------------------------

    private static string ExtractMovieTitleFromPath(string path)
    {
        string filename = Path.GetFileNameWithoutExtension(path);
        filename = ParenthesizedYearWithSpaces().Replace(filename, string.Empty);
        return filename.Trim();
    }

    private static int? ExtractYearFromPath(string path)
    {
        System.Text.RegularExpressions.Match match = ParenthesizedYear().Match(path);
        if (!match.Success)
            return null;
        return int.TryParse(match.Value.Trim('(', ')'), out int year) ? year : null;
    }
}
