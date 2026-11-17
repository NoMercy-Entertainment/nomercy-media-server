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

namespace NoMercy.MediaProcessing.Segments;

/// <summary>
/// Turns a chapter list into skip ranges. Principle: when unsure, do not skip.
/// <para>
/// <b>By title</b> (any show, when at least one title is not generic):
/// the first opening-titled chapter is the intro, the first recap-titled
/// chapter the recap. "Intro" is an opening only at 85–95 s; otherwise it is a
/// cold open and plays. The outro starts at the first ending-titled chapter in
/// the second half (or, without one, the first preview-titled chapter there)
/// and runs through the titled chapters right after it, so "ED" + "Preview"
/// become one range and an untitled epilogue after the credits still plays.
/// A last chapter is never an intro or recap: there is nothing to skip to.
/// </para>
/// <para>
/// <b>By length</b> (anime episodes whose titles are all generic):
/// the opening is an 85–95 s chapter at position 1 or 2. When position 2
/// fits, it wins: a chapter before the opening is a cold open, even one of
/// 85–95 s. A last chapter under 15 s is the next-episode preview; the
/// 85–125 s chapter right before it (or the last chapter, without a preview)
/// is the ending, in the second half. Ending + preview are one outro; a
/// preview alone is not reported.
/// </para>
/// </summary>
public class ChapterSegmentClassifier : IChapterSegmentClassifier
{
    private const double OpeningMinSeconds = 85;
    private const double OpeningMaxSeconds = 95;
    private const double PreviewMaxSeconds = 15;
    private const double EndingMinSeconds = 85;
    private const double EndingMaxSeconds = 125;
    private const int MinChaptersForLength = 3;

    public IReadOnlyList<ClassifiedChapterSegment> Classify(
        IReadOnlyList<ChapterSpan> chapters,
        bool lengthRulesApply
    )
    {
        List<ChapterSpan> usable =
        [
            .. chapters.Where(c => c.EndSeconds > c.StartSeconds).OrderBy(c => c.StartSeconds),
        ];
        if (usable.Count < 2)
            return [];

        bool allGeneric = usable.All(c => ChapterTitlePatterns.IsGeneric(c.Title));
        if (!allGeneric)
            return ByTitle(usable);

        return lengthRulesApply ? ByLength(usable) : [];
    }

    private static List<ClassifiedChapterSegment> ByTitle(List<ChapterSpan> chapters)
    {
        List<ClassifiedChapterSegment> found = [];
        int last = chapters.Count - 1;

        int opening = chapters.FindIndex(c => IsOpeningByTitle(c));
        if (opening >= 0 && opening < last)
            found.Add(Segment(ContentSegmentType.Intro, chapters[opening], chapters[opening]));

        int recap = chapters.FindIndex(c => ChapterTitlePatterns.IsRecap(c.Title));
        if (recap >= 0 && recap < last)
            found.Add(Segment(ContentSegmentType.Recap, chapters[recap], chapters[recap]));

        double halfway = chapters[last].EndSeconds / 2;
        int outroStart = chapters.FindIndex(c =>
            c.StartSeconds >= halfway && ChapterTitlePatterns.IsEnding(c.Title)
        );
        if (outroStart < 0)
            outroStart = chapters.FindIndex(c =>
                c.StartSeconds >= halfway && ChapterTitlePatterns.IsAfterEnding(c.Title)
            );

        if (outroStart >= 0)
        {
            int outroEnd = outroStart;
            while (outroEnd < last && IsOutroTitle(chapters[outroEnd + 1].Title))
                outroEnd++;

            found.Add(
                Segment(ContentSegmentType.Outro, chapters[outroStart], chapters[outroEnd])
            );
        }

        return found;
    }

    private static List<ClassifiedChapterSegment> ByLength(List<ChapterSpan> chapters)
    {
        if (chapters.Count < MinChaptersForLength)
            return [];

        List<ClassifiedChapterSegment> found = [];
        int last = chapters.Count - 1;

        int opening =
            IsOpeningLength(chapters[1]) ? 1
            : IsOpeningLength(chapters[0]) ? 0
            : -1;
        if (opening >= 0)
            found.Add(
                Segment(
                    ContentSegmentType.Intro,
                    chapters[opening],
                    chapters[opening],
                    ChapterSegmentMethod.Length
                )
            );

        bool hasPreview = chapters[last].Length < PreviewMaxSeconds;
        int ending = hasPreview ? last - 1 : last;
        double halfway = chapters[last].EndSeconds / 2;
        if (
            ending > opening
            && ending > 0
            && chapters[ending].StartSeconds >= halfway
            && Fits(chapters[ending], EndingMinSeconds, EndingMaxSeconds)
        )
            found.Add(
                Segment(
                    ContentSegmentType.Outro,
                    chapters[ending],
                    chapters[last],
                    ChapterSegmentMethod.Length
                )
            );

        return found;
    }

    private static bool IsOpeningByTitle(ChapterSpan chapter) =>
        ChapterTitlePatterns.IsOpening(chapter.Title)
        || (ChapterTitlePatterns.IsIntro(chapter.Title) && IsOpeningLength(chapter));

    private static bool IsOutroTitle(string title) =>
        ChapterTitlePatterns.IsEnding(title) || ChapterTitlePatterns.IsAfterEnding(title);

    private static bool IsOpeningLength(ChapterSpan chapter) =>
        Fits(chapter, OpeningMinSeconds, OpeningMaxSeconds);

    private static bool Fits(ChapterSpan chapter, double min, double max) =>
        chapter.Length >= min && chapter.Length <= max;

    private static ClassifiedChapterSegment Segment(
        ContentSegmentType type,
        ChapterSpan from,
        ChapterSpan through,
        ChapterSegmentMethod method = ChapterSegmentMethod.Title
    ) => new(type, from.StartSeconds, through.EndSeconds, method);
}
