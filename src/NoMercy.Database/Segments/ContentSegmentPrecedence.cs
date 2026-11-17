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

namespace NoMercy.Database.Segments;

/// <summary>
/// Applies the <see cref="ContentSegmentRank"/> order when a writer stores
/// segments, so every episode or movie keeps at most one automatic segment per
/// kind and every client reads the same list.
/// <para>
/// A kind is intro, recap, or the end of the item: <see cref="ContentSegmentType.Outro"/>
/// and <see cref="ContentSegmentType.Credits"/> are one kind, because every client
/// shows both as the same "skip credits" button.
/// </para>
/// </summary>
public static class ContentSegmentPrecedence
{
    /// <summary>Confidence of a chapter matched by its title.</summary>
    public const double ChapterTitleConfidence = 1.0;

    /// <summary>
    /// Confidence of a chapter chosen by its length. Both kinds of chapter row
    /// share <see cref="ContentSegmentSource.Chapters"/>; the confidence is what
    /// tells them apart, so it must stay below <see cref="ChapterTitleConfidence"/>.
    /// </summary>
    public const double ChapterLengthConfidence = 0.7;

    private const double SameTimeTolerance = 0.001;

    private static readonly ContentSegmentType[] Kinds =
    [
        ContentSegmentType.Intro,
        ContentSegmentType.Recap,
        ContentSegmentType.Outro,
    ];

    public static ContentSegmentRank RankOf(ContentSegment segment) =>
        RankOf(segment.Source, segment.Confidence);

    public static ContentSegmentRank RankOf(string? source, double confidence) =>
        source switch
        {
            ContentSegmentSource.Manual => ContentSegmentRank.Manual,
            ContentSegmentSource.Chapters => confidence >= ChapterTitleConfidence
                ? ContentSegmentRank.ChapterTitle
                : ContentSegmentRank.ChapterLength,
            ContentSegmentSource.Detector
            or ContentSegmentSource.Auto
            or ContentSegmentSource.Analysis => ContentSegmentRank.Detection,
            _ => ContentSegmentRank.Import,
        };

    /// <summary>
    /// The kind a segment type competes in: intro, recap, or outro (which
    /// includes credits).
    /// </summary>
    public static ContentSegmentType KindOf(ContentSegmentType type) =>
        type == ContentSegmentType.Credits ? ContentSegmentType.Outro : type;

    /// <summary>
    /// Plans one writer's store for ONE episode or movie.
    /// <list type="bullet">
    /// <item>The writer's earlier rows (<paramref name="writerSource"/>) are its
    /// own stale result and go, except a row identical to a new one, which
    /// stays so a rerun does not churn ids.</item>
    /// <item>Per kind, the best new row is stored when no other row of that kind
    /// outranks it. It then replaces the other rows of that kind it equals or
    /// outranks: the newest automatic result wins a tie.</item>
    /// <item>A manual row is never removed, and blocks every new row of its kind.</item>
    /// </list>
    /// </summary>
    /// <param name="existing">Every stored row of the item, of any source.</param>
    /// <param name="incoming">The writer's new rows for the item.</param>
    /// <param name="writerSource">The <see cref="ContentSegmentSource"/> the writer stores.</param>
    public static ContentSegmentWritePlan Plan(
        IReadOnlyList<ContentSegment> existing,
        IReadOnlyList<ContentSegment> incoming,
        string writerSource
    )
    {
        List<ContentSegment> remove = [];
        List<ContentSegment> add = [];

        foreach (ContentSegmentType kind in Kinds)
        {
            List<ContentSegment> own =
            [
                .. existing.Where(s => s.Source == writerSource && KindOf(s.SegmentType) == kind),
            ];
            List<ContentSegment> others =
            [
                .. existing.Where(s => s.Source != writerSource && KindOf(s.SegmentType) == kind),
            ];

            ContentSegment? candidate = incoming
                .Where(s => KindOf(s.SegmentType) == kind)
                .OrderByDescending(RankOf)
                .ThenBy(s => s.StartSeconds)
                .FirstOrDefault();

            if (candidate is null || !Wins(candidate, others))
            {
                remove.AddRange(own);
                continue;
            }

            remove.AddRange(others);

            ContentSegment? unchanged = own.FirstOrDefault(s => IsSameSegment(s, candidate));
            remove.AddRange(own.Where(s => !ReferenceEquals(s, unchanged)));
            if (unchanged is null)
                add.Add(candidate);
        }

        return new(remove, add);
    }

    private static bool Wins(ContentSegment candidate, List<ContentSegment> others)
    {
        ContentSegmentRank rank = RankOf(candidate);
        return others.All(other =>
            RankOf(other) != ContentSegmentRank.Manual && RankOf(other) <= rank
        );
    }

    private static bool IsSameSegment(ContentSegment stored, ContentSegment candidate) =>
        stored.SegmentType == candidate.SegmentType
        && Math.Abs(stored.StartSeconds - candidate.StartSeconds) < SameTimeTolerance
        && Math.Abs(stored.EndSeconds - candidate.EndSeconds) < SameTimeTolerance
        && Math.Abs(stored.Confidence - candidate.Confidence) < SameTimeTolerance
        // A rerun that moves only content_end (#598) is a new answer, not the same row.
        && Math.Abs((stored.ContentEndSeconds ?? -1) - (candidate.ContentEndSeconds ?? -1))
            < SameTimeTolerance;
}
