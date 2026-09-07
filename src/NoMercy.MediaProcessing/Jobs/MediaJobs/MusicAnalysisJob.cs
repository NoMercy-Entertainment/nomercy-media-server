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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NoMercy.Database;
using NoMercy.Database.Models.Music;
using NoMercy.MediaProcessing.AudioAnalysis;
using NoMercy.Storage;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.MediaProcessing.Jobs.MediaJobs;

/// <summary>
/// Measures one track and stores the result.
/// <para>
/// Lowest priority on the music queue: this is optional work that must never
/// delay an import or an encode. A library of any size will not finish in one
/// sitting, so the job is idempotent and the sweep that queues it is resumable.
/// </para>
/// </summary>
[Serializable]
public class MusicAnalysisJob : IShouldQueue
{
    [JsonIgnore]
    private readonly IAudioAnalyzer _analyzer = null!;

    [JsonIgnore]
    private readonly IStorageDriver _storageDriver = null!;

    [JsonIgnore]
    private readonly ILogger _logger = null!;

    [JsonIgnore]
    private readonly IDbContextFactory<MediaContext> _contextFactory = null!;

    public string QueueName => "music";
    public int Priority => 0;

    /// <summary>
    /// The only payload. A scalar id rather than the track, so the queue row
    /// stays small and the job always reads current state rather than whatever
    /// was true when it was queued.
    /// </summary>
    public Guid TrackId { get; set; }

    [ActivatorUtilitiesConstructor]
    public MusicAnalysisJob(
        IAudioAnalyzer analyzer,
        IStorageDriver storageDriver,
        IDbContextFactory<MediaContext> contextFactory,
        ILoggerFactory loggerFactory
    )
    {
        _analyzer = analyzer;
        _storageDriver = storageDriver;
        _contextFactory = contextFactory;
        _logger = loggerFactory.CreateLogger<MusicAnalysisJob>();
    }

    public MusicAnalysisJob()
    {
        //
    }

    public async Task Handle()
    {
        await using MediaContext mediaContext = await _contextFactory.CreateDbContextAsync();

        Track? track = await mediaContext
            .Tracks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == TrackId);

        if (track is null)
        {
            return;
        }

        TrackAudioAnalysis? existing = await mediaContext.TrackAudioAnalysis.FirstOrDefaultAsync(
            a => a.TrackId == TrackId
        );

        if (IsAlreadyDone(existing))
        {
            return;
        }

        if (
            string.IsNullOrWhiteSpace(track.HostFolder) || string.IsNullOrWhiteSpace(track.Filename)
        )
        {
            await Persist(mediaContext, existing, null, "track has no file on disk");
            return;
        }

        string path = _storageDriver.CombinePath(track.HostFolder, track.Filename);

        AudioAnalysisResult? result = await _analyzer.AnalyzeAsync(path, CancellationToken.None);

        if (result is null)
        {
            _logger.LogWarning("audio analysis produced nothing for track {TrackId}", TrackId);
            await Persist(mediaContext, existing, null, "analysis produced no measurements");
            return;
        }

        await Persist(mediaContext, existing, result, null);
    }

    /// <summary>
    /// A row already carrying this analyzer's verdict is left alone. Re-running
    /// after an analyzer bump is the point of the version, and a terminal
    /// failure at the current version is an answer, not a gap.
    /// </summary>
    private bool IsAlreadyDone(TrackAudioAnalysis? existing)
    {
        if (existing is null)
        {
            return false;
        }

        if (existing.AnalyzerVersion != _analyzer.Version)
        {
            return false;
        }

        return existing.State != AudioAnalysisState.Pending;
    }

    private async Task Persist(
        MediaContext mediaContext,
        TrackAudioAnalysis? existing,
        AudioAnalysisResult? result,
        string? failureReason
    )
    {
        TrackAudioAnalysis row = existing ?? new TrackAudioAnalysis { TrackId = TrackId };

        row.AnalyzerVersion = _analyzer.Version;
        row.AnalyzedAt = DateTime.UtcNow;
        row.FailureReason = failureReason;

        if (result is null)
        {
            row.State = AudioAnalysisState.Failed;
        }
        else
        {
            row.State = AudioAnalysisState.Ok;
            row.Bpm = result.Bpm;
            row.BpmConfidence = result.BpmConfidence;
            row.BeatOffsetMs = result.BeatOffsetMs;
            row.BeatIntervalMs = result.BeatIntervalMs;
            row.KeyName = result.KeyName;
            row.KeyCamelot = CamelotKey.FromKeyName(result.KeyName);
            row.KeyConfidence = result.KeyConfidence;
            row.IntegratedLufs = result.IntegratedLufs;
            row.TruePeakDb = result.TruePeakDb;
            row.LoudnessRange = result.LoudnessRange;
            row.SpectralCentroid = result.SpectralCentroid;
            row.Energy = AudioEnergy.Estimate(result.IntegratedLufs, result.SpectralCentroid);
            row.IntroEndMs = result.IntroEndMs;
            row.OutroStartMs = result.OutroStartMs;
        }

        if (existing is null)
        {
            mediaContext.TrackAudioAnalysis.Add(row);
        }

        await mediaContext.SaveChangesAsync();
    }

    public void Dispose() { }
}
