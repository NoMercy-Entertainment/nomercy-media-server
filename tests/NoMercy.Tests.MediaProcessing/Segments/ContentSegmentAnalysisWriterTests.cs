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
using NoMercy.Data.Repositories;
using NoMercy.Data.Services.SegmentAnalysis;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Segments;
using NoMercy.MediaProcessing.Segments;

namespace NoMercy.Tests.MediaProcessing.Segments;

/// <summary>
/// The analysis writes under the one precedence order of #503
/// (ContentSegmentPrecedence + the shared apply step): it outranks chapters
/// chosen by length and the older detector, and loses to manual rows, chapter
/// titles and imports.
/// </summary>
[Trait("Category", "Unit")]
public class ContentSegmentAnalysisWriterTests
{
    private readonly Mock<IContentSegmentRepository> _repository = new();
    private readonly List<ContentSegment> _existing = [];
    private List<ContentSegment> _written = [];
    private List<ContentSegment> _removed = [];

    public ContentSegmentAnalysisWriterTests()
    {
        _repository
            .Setup(r => r.GetForEpisodesAsync(It.IsAny<IReadOnlyList<int>>()))
            .ReturnsAsync(() => [.. _existing.Where(s => s.EpisodeId is not null)]);
        _repository
            .Setup(r => r.GetForMovieAsync(It.IsAny<int>()))
            .ReturnsAsync((int id) => [.. _existing.Where(s => s.MovieId == id)]);
        _repository
            .Setup(r => r.ApplyAsync(It.IsAny<IReadOnlyList<ContentSegmentWritePlan>>()))
            .Callback<IReadOnlyList<ContentSegmentWritePlan>>(plans =>
            {
                _written = [.. plans.SelectMany(p => p.Add)];
                _removed = [.. plans.SelectMany(p => p.Remove)];
            })
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task AnalysisOutranksChaptersByLength()
    {
        ContentSegment length = Existing(
            ContentSegmentSource.Chapters,
            ContentSegmentType.Intro,
            ContentSegmentPrecedence.ChapterLengthConfidence
        );

        await Writer().WriteAsync([Item(1)], CancellationToken.None);

        _removed.Should().Contain(length);
        _written
            .Should()
            .Contain(r =>
                r.EpisodeId == 1
                && r.SegmentType == ContentSegmentType.Intro
                && r.Source == "analysis"
            );
    }

    [Fact]
    public async Task AnalysisLosesToChaptersByTitle()
    {
        ContentSegment title = Existing(
            ContentSegmentSource.Chapters,
            ContentSegmentType.Outro,
            ContentSegmentPrecedence.ChapterTitleConfidence
        );

        await Writer().WriteAsync([Item(1)], CancellationToken.None);

        _removed.Should().NotContain(title);
        _written
            .Should()
            .NotContain(
                r => r.SegmentType == ContentSegmentType.Credits,
                "credits and outro are one kind"
            );
        _written.Should().Contain(r => r.SegmentType == ContentSegmentType.Intro);
    }

    [Fact]
    public async Task AnalysisLosesToImport()
    {
        ContentSegment import = Existing(
            ContentSegmentSource.Import,
            ContentSegmentType.Intro,
            1.0
        );

        await Writer().WriteAsync([Item(1)], CancellationToken.None);

        _removed.Should().NotContain(import);
        _written.Should().NotContain(r => r.SegmentType == ContentSegmentType.Intro);
    }

    [Fact]
    public async Task AnalysisLosesToManual()
    {
        ContentSegment manual = Existing(
            ContentSegmentSource.Manual,
            ContentSegmentType.Intro,
            1.0
        );

        await Writer().WriteAsync([Item(1)], CancellationToken.None);

        _removed.Should().NotContain(manual);
        _written.Should().NotContain(r => r.SegmentType == ContentSegmentType.Intro);
        _written.Should().Contain(r => r.SegmentType == ContentSegmentType.Credits);
    }

    [Fact]
    public async Task AnalysisReplacesTheOlderDetectorRow()
    {
        ContentSegment detector = Existing(
            ContentSegmentSource.Detector,
            ContentSegmentType.Outro,
            0.8
        );

        await Writer().WriteAsync([Item(1)], CancellationToken.None);

        _removed.Should().Contain(detector, "the newest automatic result wins a tie");
        _written.Should().Contain(r => r.SegmentType == ContentSegmentType.Credits);
    }

    [Fact]
    public async Task Season_ReplacesItsEarlierAnalysisRows()
    {
        ContentSegment old = Existing(ContentSegmentSource.Analysis, ContentSegmentType.Intro, 0.9);

        await Writer().WriteAsync([Item(1), Item(2)], CancellationToken.None);

        _removed.Should().Equal(old);
        _written.Should().HaveCount(4);
        _written!
            .Where(r => r.SegmentType == ContentSegmentType.Credits)
            .Should()
            .AllSatisfy(r => r.ContentEndSeconds.Should().Be(1377.27));
    }

    [Fact]
    public async Task Movie_IsWrittenThroughTheSameApplyStep()
    {
        await Writer()
            .WriteAsync(
                [new AnalysedItem(null, 129, SegmentAnswers.Episode())],
                CancellationToken.None
            );

        _written
            .Should()
            .HaveCount(2)
            .And.OnlyContain(r => r.MovieId == 129 && r.Source == "analysis");
    }

    private ContentSegmentAnalysisWriter Writer() =>
        new(_repository.Object, new SegmentAnalysisLocks());

    private ContentSegment Existing(string source, ContentSegmentType type, double confidence)
    {
        ContentSegment row = new()
        {
            EpisodeId = 1,
            SegmentType = type,
            Source = source,
            Confidence = confidence,
            StartSeconds = 10,
            EndSeconds = 40,
        };
        _existing.Add(row);
        return row;
    }

    private static AnalysedItem Item(int episodeId) =>
        new(episodeId, null, SegmentAnswers.Episode(episodeId.ToString()));
}
