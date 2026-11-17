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
using NoMercy.Database.Models.TvShows;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.ContentAnalysis;
using NoMercy.Encoder.Progress;
using NoMercy.Encoder.Subtitles;
using NoMercy.MediaProcessing.Jobs.SegmentJobs;
using NoMercy.MediaProcessing.Segments;
using NoMercy.Storage;
using NoMercyQueue.Core.Interfaces;
using ContentSegment = NoMercy.Database.Models.Media.ContentSegment;
using ContentSegmentType = NoMercy.Database.Models.Media.ContentSegmentType;
using VideoFile = NoMercy.Database.Models.Media.VideoFile;

namespace NoMercy.Data.Services;

/// <summary>
/// Business logic extracted from
/// <see cref="NoMercy.Api.Controllers.V1.Encoder.EncoderContentAnalysisController"/>
/// and its deprecated dashboard alias. Both controllers own auth, HTTP input
/// binding, and JSON response shaping; this service owns file resolution,
/// invoking the crop/whisper/tesseract tools, and queuing the segment
/// analysis for a season.
/// </summary>
public class ContentAnalysisService(
    ICropDetector cropDetector,
    IJobDispatcher jobDispatcher,
    ISegmentAnalysisState segmentAnalysisState,
    ISubtitleOcrEngine? ocrEngine,
    IWhisperTranscriber? whisperTranscriber,
    IVideoFileRepository videoFileRepository,
    IContentSegmentRepository contentSegmentRepository,
    IStorageDriver storageDriver
)
{
    // -------------------------------------------------------------------------
    // Crop
    // -------------------------------------------------------------------------

    public sealed record CropOutcome
    {
        public bool NotFound { get; init; }
        public string? SourceMissingPath { get; init; }
        public string? ErrorMessage { get; init; }
        public CropResult? Result { get; init; }
    }

    public async Task<CropOutcome> DetectCropAsync(Ulid videoFileId, CancellationToken ct)
    {
        VideoFile? file = await videoFileRepository.GetByIdAsync(videoFileId, ct);
        if (file is null)
            return new() { NotFound = true };

        string path = storageDriver.CombinePath(file.HostFolder, file.Filename);
        if (!storageDriver.FileExists(path))
            return new() { SourceMissingPath = path };

        Guid sourceVideoFileId = new(videoFileId.ToByteArray());

        try
        {
            CropResult result = await cropDetector.DetectAsync(
                path,
                sourceVideoFileId,
                sourceIsHdr: null,
                ct
            );
            return new() { Result = result };
        }
        catch (Exception ex)
        {
            return new() { ErrorMessage = ex.Message };
        }
    }

    // -------------------------------------------------------------------------
    // Season intro / credits analysis (on demand)
    // -------------------------------------------------------------------------

    public sealed record SeasonAnalysisRequest
    {
        public int EpisodesFound { get; init; }
        public bool Queued { get; init; }

        /// <summary>The season's stored intro now (first episode that has one), before the new run.</summary>
        public ContentSegment? Intro { get; init; }

        /// <summary>The season's stored credits or outro now, before the new run.</summary>
        public ContentSegment? Outro { get; init; }
    }

    /// <summary>
    /// Queues the #598 segment analysis for a season (the same job that runs
    /// after an encode) and answers with what is stored now. A manual request
    /// also clears the season's failure count, so a season the backfill gave up
    /// on runs again. The new answer lands in the content segments when the job
    /// has run (minutes per episode, after encodes, never during playback).
    /// </summary>
    public async Task<SeasonAnalysisRequest> QueueSegmentAnalysisForSeasonAsync(
        int seasonId,
        CancellationToken ct
    )
    {
        List<Episode> episodes = await videoFileRepository.GetEncodedEpisodesForSeasonAsync(
            seasonId,
            ct
        );
        if (episodes.Count == 0)
            return new() { EpisodesFound = 0 };

        await segmentAnalysisState.ResetFailuresAsync(SegmentAnalysisTarget.Season, seasonId, ct);
        SegmentAnalysisJob job = SegmentAnalysisJob.ForSeason(seasonId);
        jobDispatcher.Dispatch(job, job.QueueName, job.Priority);

        ContentSegment? intro = null;
        ContentSegment? outro = null;
        foreach (Episode episode in episodes.OrderBy(e => e.EpisodeNumber))
        {
            if (intro is not null && outro is not null)
                break;
            List<ContentSegment> segments = await contentSegmentRepository.GetForEpisodeAsync(
                episode.Id
            );
            intro ??= segments.FirstOrDefault(s => s.SegmentType == ContentSegmentType.Intro);
            outro ??= segments
                .Where(s =>
                    s.SegmentType is ContentSegmentType.Credits or ContentSegmentType.Outro
                    && s.StartSeconds >= 0
                )
                .OrderByDescending(s => s.StartSeconds)
                .FirstOrDefault();
        }

        return new()
        {
            EpisodesFound = episodes.Count,
            Queued = true,
            Intro = intro,
            Outro = outro,
        };
    }

    // -------------------------------------------------------------------------
    // OCR
    // -------------------------------------------------------------------------

    public sealed record OcrOutcome
    {
        public bool NotFound { get; init; }
        public string? SourceMissingPath { get; init; }
        public string? ErrorMessage { get; init; }
        public SubtitleTrack? Track { get; init; }
    }

    public async Task<OcrOutcome> OcrBitmapSubtitleAsync(
        Ulid videoFileId,
        int streamIndex,
        string language,
        SubtitleCodecType format,
        CancellationToken ct
    )
    {
        VideoFile? file = await videoFileRepository.GetByIdAsync(videoFileId, ct);
        if (file is null)
            return new() { NotFound = true };

        string path = storageDriver.CombinePath(file.HostFolder, file.Filename);
        if (!storageDriver.FileExists(path))
            return new() { SourceMissingPath = path };

        try
        {
            SubtitleTrack track = await ocrEngine!.OcrAsync(
                path,
                streamIndex,
                language,
                format,
                ct
            );
            return new() { Track = track };
        }
        catch (Exception ex)
        {
            return new() { ErrorMessage = ex.Message };
        }
    }

    // -------------------------------------------------------------------------
    // Whisper
    // -------------------------------------------------------------------------

    public sealed record WhisperOutcome
    {
        public bool NotFound { get; init; }
        public string? SourceMissingPath { get; init; }
        public string? ErrorMessage { get; init; }
        public SubtitleTrack? Track { get; init; }
    }

    public async Task<WhisperOutcome> TranscribeAsync(
        Ulid videoFileId,
        int audioStreamIndex,
        string language,
        WhisperModelSize modelSize,
        bool translateToEnglish,
        IProgressObserver? progress,
        CancellationToken ct
    )
    {
        VideoFile? file = await videoFileRepository.GetByIdAsync(videoFileId, ct);
        if (file is null)
            return new() { NotFound = true };

        string path = storageDriver.CombinePath(file.HostFolder, file.Filename);
        if (!storageDriver.FileExists(path))
            return new() { SourceMissingPath = path };

        // No ModelPath: the transcriber uses the configured whisper model.
        WhisperOptions options = new(ModelSize: modelSize, TranslateToEnglish: translateToEnglish);

        try
        {
            SubtitleTrack track = await whisperTranscriber!.TranscribeAsync(
                path,
                audioStreamIndex: audioStreamIndex,
                language: language,
                options: options,
                progress: progress,
                ct: ct
            );
            return new() { Track = track };
        }
        catch (Exception ex)
        {
            return new() { ErrorMessage = ex.Message };
        }
    }

    // -------------------------------------------------------------------------
    // Pure helpers — kept static so they are cheaply unit-testable.
    // -------------------------------------------------------------------------

    public static SubtitleCodecType ParseTargetFormat(string? raw) =>
        (raw ?? "webvtt").ToLowerInvariant() switch
        {
            "vtt" or "webvtt" => SubtitleCodecType.WebVtt,
            "ass" => SubtitleCodecType.Ass,
            "srt" => SubtitleCodecType.Srt,
            _ => SubtitleCodecType.WebVtt,
        };
}
