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
/// The interim write rule at the #503 precedence seam: analysis never
/// overwrites a manual or chapters segment of the same kind, and a rerun
/// replaces its own earlier rows, all through the shared apply step.
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
    public async Task Season_ReplacesItsEarlierAnalysisRows()
    {
        ContentSegment old = new() { EpisodeId = 1, SegmentType = ContentSegmentType.Intro, Source = "analysis" };
        _existing.Add(old);

        await Writer().WriteAsync([Item(1), Item(2)], CancellationToken.None);

        _removed.Should().Equal(old);
        _written.Should().HaveCount(4);
        _written!
            .Where(r => r.SegmentType == ContentSegmentType.Credits)
            .Should()
            .AllSatisfy(r => r.ContentEndSeconds.Should().Be(1377.27));
    }

    [Fact]
    public async Task ManualIntro_BlocksTheAnalysisIntro_ButNotItsCredits()
    {
        _existing.Add(new ContentSegment { EpisodeId = 1, SegmentType = ContentSegmentType.Intro, Source = "manual" });

        await Writer().WriteAsync([Item(1), Item(2)], CancellationToken.None);

        _written.Should().NotContain(r => r.EpisodeId == 1 && r.SegmentType == ContentSegmentType.Intro);
        _written.Should().Contain(r => r.EpisodeId == 1 && r.SegmentType == ContentSegmentType.Credits);
        _written.Should().Contain(r => r.EpisodeId == 2 && r.SegmentType == ContentSegmentType.Intro);
        _removed.Should().BeEmpty();
    }

    [Fact]
    public async Task ChaptersOutro_BlocksTheAnalysisCredits_TheyAreOneKind()
    {
        _existing.Add(new ContentSegment { EpisodeId = 2, SegmentType = ContentSegmentType.Outro, Source = "chapters" });

        await Writer().WriteAsync([Item(1), Item(2)], CancellationToken.None);

        _written.Should().NotContain(r => r.EpisodeId == 2 && r.SegmentType == ContentSegmentType.Credits);
        _written.Should().Contain(r => r.EpisodeId == 2 && r.SegmentType == ContentSegmentType.Intro);
    }

    [Fact]
    public async Task Movie_IsWrittenThroughTheSameApplyStep()
    {
        await Writer().WriteAsync([new AnalysedItem(null, 129, SegmentAnswers.Episode())], CancellationToken.None);

        _written.Should().HaveCount(2).And.OnlyContain(r => r.MovieId == 129 && r.Source == "analysis");
    }

    private ContentSegmentAnalysisWriter Writer() => new(_repository.Object);

    private static AnalysedItem Item(int episodeId) => new(episodeId, null, SegmentAnswers.Episode(episodeId.ToString()));
}
