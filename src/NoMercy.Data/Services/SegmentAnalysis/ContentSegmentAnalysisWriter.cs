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

using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Segments;
using NoMercy.MediaProcessing.Segments;

namespace NoMercy.Data.Services.SegmentAnalysis;

/// <inheritdoc />
public sealed class ContentSegmentAnalysisWriter(IContentSegmentRepository repository)
    : ISegmentAnalysisWriter
{
    private const string ManualSource = "manual";
    private const string ChaptersSource = "chapters";

    public async Task WriteAsync(IReadOnlyList<AnalysedItem> items, CancellationToken ct)
    {
        List<int> episodeIds = [.. items.Where(i => i.EpisodeId is not null).Select(i => i.EpisodeId!.Value)];
        List<ContentSegment> existing = episodeIds.Count > 0 ? await repository.GetForEpisodesAsync(episodeIds) : [];
        foreach (int movieId in items.Where(i => i.MovieId is not null).Select(i => i.MovieId!.Value))
        {
            ct.ThrowIfCancellationRequested();
            existing.AddRange(await repository.GetForMovieAsync(movieId));
        }

        List<ContentSegmentWritePlan> plans = [];
        foreach (AnalysedItem item in items)
        {
            List<ContentSegment> mine =
            [
                .. existing.Where(s => s.EpisodeId == item.EpisodeId && s.MovieId == item.MovieId),
            ];
            List<ContentSegment> stronger = [.. mine.Where(s => s.Source is ManualSource or ChaptersSource)];
            List<ContentSegment> stale = [.. mine.Where(s => s.Source == AnalysisSegmentRows.Source)];
            plans.Add(new(stale, Unblocked(AnalysisSegmentRows.For(item.EpisodeId, item.MovieId, item.Answer), stronger)));
        }

        await repository.ApplyAsync(plans);
    }

    /// <summary>
    /// PRECEDENCE SEAM (#503). Interim rule until the analysis is routed through
    /// #503's single precedence helper (ContentSegmentPrecedence.Plan(existing,
    /// incoming, "analysis") per item): an analysis row is written only when the
    /// item has no manual and no chapters row of the same kind. Intro is one
    /// kind; Outro and Credits are one kind (the same "skip credits" button).
    /// Older "detector" and "auto" rows are left as they are: clients keep
    /// reading them until the precedence helper replaces them.
    /// </summary>
    internal static List<ContentSegment> Unblocked(
        List<ContentSegment> rows,
        IReadOnlyList<ContentSegment> stronger
    )
    {
        HashSet<(int?, int?, ContentSegmentType)> blocked =
        [
            .. stronger.Select(s => (s.EpisodeId, s.MovieId, KindOf(s.SegmentType))),
        ];
        return [.. rows.Where(r => !blocked.Contains((r.EpisodeId, r.MovieId, KindOf(r.SegmentType))))];
    }

    private static ContentSegmentType KindOf(ContentSegmentType type) =>
        type == ContentSegmentType.Credits ? ContentSegmentType.Outro : type;
}
