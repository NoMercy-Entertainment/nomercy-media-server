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
using Moq;
using NoMercy.Data.Repositories;
using NoMercy.Data.Services;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.TvShows;
using NoMercy.Database.Segments;
using NoMercy.Encoder.ContentAnalysis.Fingerprinting;
using NoMercy.Storage;
using ContentSegment = NoMercy.Database.Models.Media.ContentSegment;
using ContentSegmentType = NoMercy.Database.Models.Media.ContentSegmentType;
using ICropDetector = NoMercy.Encoder.ContentAnalysis.ICropDetector;

namespace NoMercy.Tests.Repositories.Services;

/// <summary>
/// The encoder analysis endpoint stores its "auto" detection under the
/// precedence order: a manual segment or a chapter title keeps its kind.
/// </summary>
[Trait("Category", "Unit")]
public class ContentAnalysisServicePrecedenceTests
{
    [Fact]
    public async Task Detection_fills_only_the_kinds_no_manual_or_chapter_title_holds()
    {
        List<Episode> episodes = [Episode(1), Episode(2)];
        List<ContentSegment> stored =
        [
            Stored(1, ContentSegmentSource.Manual, ContentSegmentType.Intro, 1.0),
            Stored(2, ContentSegmentSource.Chapters, ContentSegmentType.Outro, 1.0),
            Stored(2, ContentSegmentSource.Chapters, ContentSegmentType.Intro, 0.7),
        ];

        Mock<IVideoFileRepository> videoFiles = new();
        videoFiles
            .Setup(r => r.GetEncodedEpisodesForSeasonAsync(9, It.IsAny<CancellationToken>()))
            .ReturnsAsync(episodes);

        Mock<IStorageDriver> driver = new();
        driver
            .Setup(d => d.CombinePath(It.IsAny<string>(), It.IsAny<string>()))
            .Returns("/media/ep.mkv");
        driver.Setup(d => d.FileExists(It.IsAny<string>())).Returns(true);

        Mock<IAudioFingerprinter> fingerprinter = new();
        fingerprinter
            .Setup(f =>
                f.FingerprintAsync(
                    It.IsAny<string>(),
                    It.IsAny<FingerprintWindow?>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<IStorage?>()
                )
            )
            .ReturnsAsync(
                new AudioFingerprint([1, 2, 3], TimeSpan.FromMilliseconds(124), TimeSpan.Zero)
            );

        Mock<IIntroDetector> detector = new();
        detector
            .Setup(d => d.DetectIntro(It.IsAny<IReadOnlyList<AudioFingerprint>>()))
            .Returns(new IntroMarker(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(95), 0.9));
        detector
            .Setup(d => d.DetectOutro(It.IsAny<IReadOnlyList<AudioFingerprint>>()))
            .Returns(new IntroMarker(TimeSpan.FromSeconds(1280), TimeSpan.FromSeconds(1370), 0.9));

        List<ContentSegmentWritePlan> applied = [];
        Mock<IContentSegmentRepository> segments = new();
        segments
            .Setup(r => r.GetForEpisodesAsync(It.IsAny<IReadOnlyList<int>>()))
            .ReturnsAsync(stored);
        segments
            .Setup(r => r.ApplyAsync(It.IsAny<IReadOnlyList<ContentSegmentWritePlan>>()))
            .Callback<IReadOnlyList<ContentSegmentWritePlan>>(plans => applied.AddRange(plans))
            .Returns(Task.CompletedTask);

        ContentAnalysisService service = new(
            Mock.Of<ICropDetector>(),
            fingerprinter.Object,
            detector.Object,
            null,
            null,
            videoFiles.Object,
            segments.Object,
            driver.Object
        );

        await service.DetectIntroForSeasonAsync(9, persistToDatabase: true, CancellationToken.None);

        List<ContentSegment> added = [.. applied.SelectMany(p => p.Add)];
        added.Should().OnlyContain(s => s.Source == ContentSegmentSource.Auto);
        added
            .Select(s => (s.EpisodeId, s.SegmentType))
            .Should()
            .BeEquivalentTo([(1, ContentSegmentType.Outro), (2, ContentSegmentType.Intro)]);
        applied
            .SelectMany(p => p.Remove)
            .Should()
            .ContainSingle()
            .Which.Confidence.Should()
            .Be(0.7, "the chapter chosen by length ranks below detection");
    }

    private static Episode Episode(int id) =>
        new()
        {
            Id = id,
            VideoFiles =
            [
                new VideoFile
                {
                    Id = Ulid.NewUlid(),
                    EpisodeId = id,
                    HostFolder = "/media",
                    Filename = $"ep{id}.mkv",
                    Duration = "00:23:00",
                },
            ],
        };

    private static ContentSegment Stored(
        int episodeId,
        string source,
        ContentSegmentType type,
        double confidence
    ) =>
        new()
        {
            EpisodeId = episodeId,
            Source = source,
            SegmentType = type,
            Confidence = confidence,
            StartSeconds = type == ContentSegmentType.Intro ? 0 : 1280,
            EndSeconds = type == ContentSegmentType.Intro ? 90 : 1370,
        };
}
