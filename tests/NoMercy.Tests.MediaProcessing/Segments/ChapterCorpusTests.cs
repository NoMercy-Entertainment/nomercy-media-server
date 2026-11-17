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

using NoMercy.Database.Models.Media;
using NoMercy.MediaProcessing.Segments;
using NoMercy.NmSystem.Domain;

namespace NoMercy.Tests.MediaProcessing.Segments;

/// <summary>
/// The acceptance corpus of #503: every episode gets exactly its expected
/// segments, and no segment covers a cold open or other story.
/// </summary>
[Trait("Category", "Unit")]
public class ChapterCorpusTests
{
    private const double Tolerance = 0.001;
    private readonly ChapterSegmentClassifier _classifier = new();

    public static TheoryData<string, string> Episodes()
    {
        TheoryData<string, string> data = new();
        foreach (string file in ChapterCorpus.Files)
        foreach (CorpusEpisode episode in ChapterCorpus.Load(file).Episodes)
            data.Add(file, episode.Episode);
        return data;
    }

    [Theory]
    [MemberData(nameof(Episodes))]
    public void Every_episode_gets_its_expected_segments(string file, string episode)
    {
        CorpusSeries series = ChapterCorpus.Load(file);
        CorpusEpisode entry = ChapterCorpus.Episode(file, episode);

        IReadOnlyList<ClassifiedChapterSegment> found = Classify(series, entry);

        found.Should().HaveSameCount(entry.Expected);
        foreach (CorpusSegment expected in entry.Expected)
            found
                .Should()
                .ContainSingle(s =>
                    s.Type == expected.Type
                    && Math.Abs(s.StartSeconds - expected.Start) < Tolerance
                    && Math.Abs(s.EndSeconds - expected.End) < Tolerance
                );
    }

    [Theory]
    [MemberData(nameof(Episodes))]
    public void No_segment_ever_covers_a_cold_open_or_other_story(string file, string episode)
    {
        CorpusSeries series = ChapterCorpus.Load(file);
        CorpusEpisode entry = ChapterCorpus.Episode(file, episode);

        IReadOnlyList<ClassifiedChapterSegment> found = Classify(series, entry);

        foreach (CorpusSegment story in entry.Story)
            found
                .Should()
                .NotContain(
                    s => s.StartSeconds < story.End && s.EndSeconds > story.Start,
                    "{0} is story ({1})",
                    $"{story.Start}-{story.End}",
                    story.Note
                );
    }

    [Fact]
    public void The_anime_corpus_holds_both_shapes_and_every_episode()
    {
        List<CorpusSeries> anime =
        [
            .. ChapterCorpus
                .Files.Select(ChapterCorpus.Load)
                .Where(s => s.LibraryType == MediaTypes.AnimeMediaType),
        ];

        anime.Sum(s => s.Episodes.Count).Should().Be(23);
        anime
            .SelectMany(s => s.Episodes)
            .SelectMany(e => e.Expected)
            .Select(e => e.Type)
            .Should()
            .Contain([ContentSegmentType.Intro, ContentSegmentType.Outro]);
    }

    private IReadOnlyList<ClassifiedChapterSegment> Classify(
        CorpusSeries series,
        CorpusEpisode episode
    ) =>
        _classifier.Classify(
            ChapterCorpus.StoredChapters(episode),
            lengthRulesApply: series.LibraryType == MediaTypes.AnimeMediaType
        );
}
