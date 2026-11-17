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

using System.Globalization;
using Microsoft.Extensions.Logging;
using NoMercy.Encoder.ContentAnalysis.Segments;
using NoMercy.Encoder.ContentAnalysis.Segments.Probes;

namespace NoMercy.MediaProcessing.Segments;

/// <inheritdoc />
public sealed class SegmentAnalysisService(
    ISegmentSourceResolver resolver,
    ISeasonSegmentPipeline pipeline,
    ISegmentAnalysisWriter writer,
    ILogger<SegmentAnalysisService> logger
) : ISegmentAnalysisService
{
    public async Task<bool> AnalyseSeasonAsync(int seasonId, CancellationToken ct)
    {
        List<ResolvedSegmentSource> sources = await resolver.ResolveSeasonAsync(seasonId, ct);
        if (sources.Count == 0)
        {
            logger.LogDebug("Segment analysis: season {SeasonId} has no readable encoded episodes", seasonId);
            return false;
        }

        Dictionary<string, int> episodeByKey = sources.ToDictionary(
            s => Key(s.EpisodeId!.Value),
            s => s.EpisodeId!.Value
        );
        IReadOnlyList<EpisodeSegments> answers = await pipeline.AnalyseAsync(
            [.. sources.Select(s => new SegmentAnalysisInput(Key(s.EpisodeId!.Value), s.Source))],
            ct
        );

        List<AnalysedItem> items =
        [
            .. answers.Select(a => new AnalysedItem(episodeByKey[a.Key], null, a)),
        ];
        await writer.WriteAsync(items, ct);
        logger.LogInformation(
            "Segment analysis: season {SeasonId}, {Count} episode(s) stored",
            seasonId,
            items.Count
        );
        return items.Count > 0;
    }

    public async Task<bool> AnalyseMovieAsync(int movieId, CancellationToken ct)
    {
        ResolvedSegmentSource? source = await resolver.ResolveMovieAsync(movieId, ct);
        if (source is null)
            return false;

        IReadOnlyList<EpisodeSegments> answers = await pipeline.AnalyseAsync(
            [new SegmentAnalysisInput(Key(movieId), source.Source)],
            ct
        );
        if (answers.Count == 0)
            return false;

        await writer.WriteAsync([new AnalysedItem(null, movieId, answers[0])], ct);
        return true;
    }

    private static string Key(int id) => id.ToString(CultureInfo.InvariantCulture);
}
