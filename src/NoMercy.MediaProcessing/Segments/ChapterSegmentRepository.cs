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
using Newtonsoft.Json;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Segments;
using NoMercy.NmSystem.Domain;

namespace NoMercy.MediaProcessing.Segments;

/// <summary>
/// Reads the chapter list the scan stored for each video file (the parsed
/// chapters.vtt that clients get with the playlist) and writes the segments
/// classified from it. When an item has several video files, the newest one
/// counts, as everywhere else on the server. An item without any video file
/// gets an empty chapter list, so its old chapter rows are removed.
/// </summary>
public class ChapterSegmentRepository(IDbContextFactory<MediaContext> contextFactory)
    : IChapterSegmentRepository
{
    private const string EmptyJsonValue = "null";
    private const string EmptyJsonList = "[]";

    public async Task<List<ChapterSource>> GetEpisodeSourcesAsync(
        IReadOnlyList<int> episodeIds,
        CancellationToken ct
    )
    {
        if (episodeIds.Count == 0)
            return [];

        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        List<StoredChapters> rows = await context
            .VideoFiles.AsNoTracking()
            .Where(vf => vf.EpisodeId != null && episodeIds.Contains(vf.EpisodeId.Value))
            .Select(vf => new StoredChapters(
                vf.Id,
                vf.EpisodeId!.Value,
                vf.Metadata != null ? vf.Metadata._chapters : null,
                vf.Episode!.Tv.Library.Type
            ))
            .ToListAsync(ct);

        List<ChapterSource> sources =
        [
            .. Newest(rows)
                .Select(row => new ChapterSource(
                    row.MediaId,
                    null,
                    row.LibraryType == MediaTypes.AnimeMediaType,
                    ParseChapters(row.ChaptersJson)
                )),
        ];
        HashSet<int> withFiles = [.. rows.Select(row => row.MediaId)];
        sources.AddRange(
            episodeIds
                .Where(id => !withFiles.Contains(id))
                .Distinct()
                .Select(id => new ChapterSource(id, null, false, []))
        );
        return sources;
    }

    public async Task<List<ChapterSource>> GetMovieSourcesAsync(
        IReadOnlyList<int> movieIds,
        CancellationToken ct
    )
    {
        if (movieIds.Count == 0)
            return [];

        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        List<StoredChapters> rows = await context
            .VideoFiles.AsNoTracking()
            .Where(vf => vf.MovieId != null && movieIds.Contains(vf.MovieId.Value))
            .Select(vf => new StoredChapters(
                vf.Id,
                vf.MovieId!.Value,
                vf.Metadata != null ? vf.Metadata._chapters : null,
                null
            ))
            .ToListAsync(ct);

        // The length rules describe anime episodes; a movie is classified by title only.
        List<ChapterSource> sources =
        [
            .. Newest(rows)
                .Select(row => new ChapterSource(
                    null,
                    row.MediaId,
                    false,
                    ParseChapters(row.ChaptersJson)
                )),
        ];
        HashSet<int> withFiles = [.. rows.Select(row => row.MediaId)];
        sources.AddRange(
            movieIds
                .Where(id => !withFiles.Contains(id))
                .Distinct()
                .Select(id => new ChapterSource(null, id, false, []))
        );
        return sources;
    }

    public async Task<List<int>> GetEpisodeIdsToClassifyAsync(CancellationToken ct)
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        List<int> withChapters = await context
            .VideoFiles.AsNoTracking()
            .Where(vf =>
                vf.EpisodeId != null
                && vf.Metadata != null
                && vf.Metadata._chapters != null
                && vf.Metadata._chapters != EmptyJsonValue
                && vf.Metadata._chapters != EmptyJsonList
            )
            .Select(vf => vf.EpisodeId!.Value)
            .Distinct()
            .ToListAsync(ct);

        List<int> withSegments = await context
            .ContentSegments.AsNoTracking()
            .Where(cs => cs.EpisodeId != null && cs.Source == ContentSegmentSource.Chapters)
            .Select(cs => cs.EpisodeId!.Value)
            .Distinct()
            .ToListAsync(ct);

        return [.. withChapters.Union(withSegments).Order()];
    }

    public async Task<List<int>> GetMovieIdsToClassifyAsync(CancellationToken ct)
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        List<int> withChapters = await context
            .VideoFiles.AsNoTracking()
            .Where(vf =>
                vf.MovieId != null
                && vf.Metadata != null
                && vf.Metadata._chapters != null
                && vf.Metadata._chapters != EmptyJsonValue
                && vf.Metadata._chapters != EmptyJsonList
            )
            .Select(vf => vf.MovieId!.Value)
            .Distinct()
            .ToListAsync(ct);

        List<int> withSegments = await context
            .ContentSegments.AsNoTracking()
            .Where(cs => cs.MovieId != null && cs.Source == ContentSegmentSource.Chapters)
            .Select(cs => cs.MovieId!.Value)
            .Distinct()
            .ToListAsync(ct);

        return [.. withChapters.Union(withSegments).Order()];
    }

    public async Task<List<ContentSegment>> GetSegmentsAsync(
        IReadOnlyList<int> episodeIds,
        IReadOnlyList<int> movieIds,
        CancellationToken ct
    )
    {
        if (episodeIds.Count == 0 && movieIds.Count == 0)
            return [];

        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        return await context
            .ContentSegments.AsNoTracking()
            .Where(cs =>
                (cs.EpisodeId != null && episodeIds.Contains(cs.EpisodeId.Value))
                || (cs.MovieId != null && movieIds.Contains(cs.MovieId.Value))
            )
            .ToListAsync(ct);
    }

    public async Task ApplyAsync(IReadOnlyList<ContentSegmentWritePlan> plans, CancellationToken ct)
    {
        if (plans.All(plan => plan.IsEmpty))
            return;

        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        await context.ApplySegmentPlansAsync(plans, ct);
    }

    private static IEnumerable<StoredChapters> Newest(List<StoredChapters> rows) =>
        rows.GroupBy(row => row.MediaId)
            .Select(group => group.OrderByDescending(row => row.VideoFileId).First());

    private static List<ChapterSpan> ParseChapters(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            List<IChapter>? chapters = JsonConvert.DeserializeObject<List<IChapter>>(json);
            return [.. (chapters ?? []).Select(ChapterSpan.FromStored)];
        }
        catch (JsonException)
        {
            // An unreadable list classifies as no chapters, like a missing chapters.vtt.
            return [];
        }
    }

    private sealed record StoredChapters(
        Ulid VideoFileId,
        int MediaId,
        string? ChaptersJson,
        string? LibraryType
    );
}
