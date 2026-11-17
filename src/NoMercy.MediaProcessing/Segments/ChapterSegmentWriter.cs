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
using NoMercy.Database.Segments;

namespace NoMercy.MediaProcessing.Segments;

public class ChapterSegmentWriter(
    IChapterSegmentRepository repository,
    IChapterSegmentClassifier classifier
) : IChapterSegmentWriter
{
    private const int BatchSize = 200;

    public async Task<int> ClassifyEpisodeAsync(int episodeId, CancellationToken ct) =>
        await WriteAsync(await repository.GetEpisodeSourcesAsync([episodeId], ct), ct);

    public async Task<int> ClassifyMovieAsync(int movieId, CancellationToken ct) =>
        await WriteAsync(await repository.GetMovieSourcesAsync([movieId], ct), ct);

    public async Task<int> ClassifyAllAsync(CancellationToken ct)
    {
        int changed = 0;

        List<int> episodeIds = await repository.GetEpisodeIdsToClassifyAsync(ct);
        foreach (int[] batch in episodeIds.Chunk(BatchSize))
            changed += await WriteAsync(await repository.GetEpisodeSourcesAsync(batch, ct), ct);

        List<int> movieIds = await repository.GetMovieIdsToClassifyAsync(ct);
        foreach (int[] batch in movieIds.Chunk(BatchSize))
            changed += await WriteAsync(await repository.GetMovieSourcesAsync(batch, ct), ct);

        return changed;
    }

    private async Task<int> WriteAsync(List<ChapterSource> sources, CancellationToken ct)
    {
        if (sources.Count == 0)
            return 0;

        List<int> episodeIds = [.. sources.Where(s => s.EpisodeId is not null).Select(s => s.EpisodeId!.Value)];
        List<int> movieIds = [.. sources.Where(s => s.MovieId is not null).Select(s => s.MovieId!.Value)];
        List<ContentSegment> stored = await repository.GetSegmentsAsync(episodeIds, movieIds, ct);

        List<ContentSegmentWritePlan> plans = [];
        foreach (ChapterSource source in sources)
        {
            List<ContentSegment> existing =
            [
                .. stored.Where(s =>
                    source.EpisodeId is not null
                        ? s.EpisodeId == source.EpisodeId
                        : s.MovieId == source.MovieId
                ),
            ];
            List<ContentSegment> incoming =
            [
                .. classifier
                    .Classify(source.Chapters, source.LengthRulesApply)
                    .Select(found => ToRow(source, found)),
            ];

            ContentSegmentWritePlan plan = ContentSegmentPrecedence.Plan(
                existing,
                incoming,
                ContentSegmentSource.Chapters
            );
            if (!plan.IsEmpty)
                plans.Add(plan);
        }

        await repository.ApplyAsync(plans, ct);
        return plans.Count;
    }

    private static ContentSegment ToRow(ChapterSource source, ClassifiedChapterSegment found) =>
        new()
        {
            EpisodeId = source.EpisodeId,
            MovieId = source.MovieId,
            SegmentType = found.Type,
            StartSeconds = found.StartSeconds,
            EndSeconds = found.EndSeconds,
            Source = ContentSegmentSource.Chapters,
            Confidence = found.Confidence,
        };
}
