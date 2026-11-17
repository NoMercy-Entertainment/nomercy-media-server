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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NoMercy.MediaProcessing.Segments;
using NoMercyQueue;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.MediaProcessing.Jobs.SegmentJobs;

/// <summary>
/// Finds the intro, the credits and the real end of content (#598) for one
/// season as a group, or for one movie (credits and end of content only).
/// Queued after an encode, by the library-scan backfill and by the on-demand
/// endpoint. Runs on the encoder queue, which holds off while anything is
/// playing, at the lowest priority there so encodes and subtitle OCR go first.
/// <para>
/// The item is marked analysed only when the analysis really ran. Nothing to
/// read (an unmounted folder) leaves it unmarked for the next scan; a run that
/// measured nothing, measured only part of a season (its answers are still
/// stored) or threw counts as a failure, and after
/// <see cref="ISegmentAnalysisState.MaxFailures"/> of them the backfill leaves
/// the item alone until its files change or someone asks for a run. A failure
/// is not rethrown: a queue retry would repeat the same expensive passes.
/// Shutdown cancels a run mid-way.
/// </para>
/// </summary>
[Serializable]
public class SegmentAnalysisJob : IShouldQueue, IJobStorageInjector
{
    public string QueueName => "encoder";
    public int Priority => 1;

    public int? SeasonId { get; set; }
    public int? MovieId { get; set; }

    /// <summary>What the tasks panel calls this run ("Hellsing, season 1"); empty when unknown.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The panel's name for the run: its title, or the item's id.</summary>
    [JsonIgnore]
    public string DisplayTitle =>
        Title.Length > 0 ? Title
        : SeasonId is not null ? $"Season {SeasonId}"
        : $"Movie {MovieId}";

    /// <summary>A stable key for the panel's list: one per season or movie.</summary>
    [JsonIgnore]
    public string Target => SeasonId is not null ? $"season:{SeasonId}" : $"movie:{MovieId}";

    private ISegmentAnalysisService? _analysis;
    private ISegmentAnalysisState? _state;
    private ILogger<SegmentAnalysisJob>? _logger;
    private CancellationToken _stopping = CancellationToken.None;

    public SegmentAnalysisJob() { }

    public static SegmentAnalysisJob ForSeason(int seasonId, string title = "") =>
        new() { SeasonId = seasonId, Title = title };

    public static SegmentAnalysisJob ForMovie(int movieId, string title = "") =>
        new() { MovieId = movieId, Title = title };

    public void InjectStorageServices(IServiceProvider serviceProvider)
    {
        _analysis = serviceProvider.GetService<ISegmentAnalysisService>();
        _state = serviceProvider.GetService<ISegmentAnalysisState>();
        _logger = serviceProvider.GetService<ILogger<SegmentAnalysisJob>>();
        _stopping =
            serviceProvider.GetService<IHostApplicationLifetime>()?.ApplicationStopping
            ?? CancellationToken.None;
    }

    public async Task Handle()
    {
        if (_analysis is null)
            return;

        (SegmentAnalysisTarget target, int id) = SeasonId is { } seasonId
            ? (SegmentAnalysisTarget.Season, seasonId)
            : MovieId is { } movieId
                ? (SegmentAnalysisTarget.Movie, movieId)
                : (SegmentAnalysisTarget.Season, -1);
        if (id < 0)
            return;

        SegmentAnalysisOutcome outcome;
        try
        {
            outcome = target == SegmentAnalysisTarget.Season
                ? await _analysis.AnalyseSeasonAsync(id, _stopping)
                : await _analysis.AnalyseMovieAsync(id, _stopping);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            int failures = await RecordFailureAsync(target, id);
            _logger?.LogError(
                ex,
                "Segment analysis of {Target} {Id} failed ({Failures}/{Max})",
                target,
                id,
                failures,
                ISegmentAnalysisState.MaxFailures
            );
            return;
        }

        switch (outcome)
        {
            case SegmentAnalysisOutcome.Analysed:
                if (_state is not null)
                    await _state.MarkAnalysedAsync(target, id, _stopping);
                break;
            case SegmentAnalysisOutcome.NothingMeasured:
            case SegmentAnalysisOutcome.PartlyMeasured:
                // Partly: the measured episodes are stored, the rest is retried.
                await RecordFailureAsync(target, id);
                break;
            case SegmentAnalysisOutcome.NoSources:
                // Unmounted or not encoded yet: the next scan tries again.
                break;
        }
    }

    private async Task<int> RecordFailureAsync(SegmentAnalysisTarget target, int id) =>
        _state is null ? 0 : await _state.RecordFailureAsync(target, id, _stopping);
}
