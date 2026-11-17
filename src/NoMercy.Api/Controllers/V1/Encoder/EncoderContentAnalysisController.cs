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

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using NoMercy.Api.Hubs;
using NoMercy.Data.Repositories;
using NoMercy.Data.Services;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.ContentAnalysis;
using NoMercy.Encoder.Subtitles;
using ContentSegment = NoMercy.Database.Models.Media.ContentSegment;

namespace NoMercy.Api.Controllers.V1.Encoder;

/// <summary>
/// On-demand content-analysis probes under the /encoder namespace.
/// These are the canonical routes going forward; the
/// /dashboard/content-analysis equivalents are kept as deprecated aliases.
/// All operations are owner-only — they invoke ffmpeg / whisper / tesseract
/// which are expensive and potentially long-running.
/// </summary>
[ApiController]
[Tags("Encoder Content Analysis")]
[ApiVersion(1.0)]
[Authorize(Policy = "Owner")]
[Route("api/v{version:apiVersion}/encoder/content-analysis")]
public class EncoderContentAnalysisController(
    ContentAnalysisService contentAnalysisService,
    ISubtitleOcrEngine? ocrEngine,
    IWhisperTranscriber? whisperTranscriber,
    IVideoFileRepository videoFileRepository,
    IContentSegmentRepository contentSegmentRepository,
    IHubContext<ContentAnalysisHub> hubContext
) : BaseController
{
    // ─── Crop ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the crop detector against a VideoFile and returns the detected
    /// rectangle. Returns <c>should_crop=false</c> when no letterbox is found.
    /// Ffmpeg-bound — can take up to 60 s on large sources.
    /// </summary>
    [HttpPost("crop/{videoFileId}")]
    public async Task<IActionResult> DetectCrop(string videoFileId, CancellationToken ct)
    {
        if (!Ulid.TryParse(videoFileId, out Ulid fileId))
            return BadRequestResponse("Invalid video file id");

        ContentAnalysisService.CropOutcome outcome = await contentAnalysisService.DetectCropAsync(
            fileId,
            ct
        );

        if (outcome.NotFound)
            return NotFoundResponse("Video file not found");
        if (outcome.SourceMissingPath is not null)
            return NotFoundResponse($"Source file missing on disk: {outcome.SourceMissingPath}");
        if (outcome.ErrorMessage is not null)
            return InternalServerErrorResponse($"Crop detection failed: {outcome.ErrorMessage}");

        CropResult result = outcome.Result!;
        return Ok(
            new
            {
                source_video_file_id = result.SourceVideoFileId,
                should_crop = result.ShouldCrop,
                width = result.Width,
                height = result.Height,
                x = result.X,
                y = result.Y,
                sample_frames_analyzed = result.SampleFramesAnalyzed,
                confidence = result.Confidence,
            }
        );
    }

    // ─── Intro / outro fingerprinting ────────────────────────────────────────

    /// <summary>
    /// Queues the intro / credits / end-of-content analysis (#598) for a season
    /// and answers at once with the segments stored now (season-level, from
    /// the first episode that has them), plus <c>status = "queued"</c>. The new
    /// answer replaces them as content segments when the job has run. Same
    /// fields as before; <c>source</c> is the stored row's, and
    /// <c>fingerprint_hash</c> is always null (the old detector's digest).
    /// </summary>
    [HttpPost("intro/{seasonId:int}")]
    public async Task<IActionResult> DetectIntroForSeason(int seasonId, CancellationToken ct)
    {
        int showId = await videoFileRepository.GetShowIdForSeasonAsync(seasonId, ct);

        ContentAnalysisService.SeasonAnalysisRequest request =
            await contentAnalysisService.QueueSegmentAnalysisForSeasonAsync(seasonId, ct);

        if (!request.Queued)
            return BadRequestResponse("The season has no encoded episodes");

        List<object> contentSegments = [];
        foreach (
            (string type, ContentSegment? segment) in new[]
            {
                ("intro", request.Intro),
                ("outro", request.Outro),
            }
        )
        {
            if (segment is null)
                continue;
            contentSegments.Add(
                new
                {
                    show_id = showId,
                    season_id = seasonId,
                    type,
                    start_seconds = segment.StartSeconds,
                    end_seconds = segment.EndSeconds,
                    confidence = segment.Confidence,
                    source = segment.Source,
                    fingerprint_hash = (string?)null,
                }
            );
        }

        return Ok(
            new
            {
                show_id = showId,
                season_id = seasonId,
                episodes_scanned = request.EpisodesFound,
                content_segments = contentSegments,
                status = "queued",
            }
        );
    }

    // ─── OCR ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs subtitle OCR on a bitmap subtitle stream (PGS / VobSub / DVB)
    /// inside the requested VideoFile.
    /// </summary>
    [HttpPost("ocr/{videoFileId}")]
    public async Task<IActionResult> OcrBitmapSubtitle(
        string videoFileId,
        [FromBody] OcrRequest body,
        CancellationToken ct
    )
    {
        if (ocrEngine is null)
            return NotImplementedResponse("Subtitle OCR engine is not registered on this build");

        if (!Ulid.TryParse(videoFileId, out Ulid fileId))
            return BadRequestResponse("Invalid video file id");

        if (string.IsNullOrWhiteSpace(body.Language))
            return BadRequestResponse("language is required");

        SubtitleCodecType format = ContentAnalysisService.ParseTargetFormat(body.TargetFormat);

        ContentAnalysisService.OcrOutcome outcome =
            await contentAnalysisService.OcrBitmapSubtitleAsync(
                fileId,
                body.StreamIndex,
                body.Language,
                format,
                ct
            );

        if (outcome.NotFound)
            return NotFoundResponse("Video file not found");
        if (outcome.SourceMissingPath is not null)
            return NotFoundResponse($"Source file missing on disk: {outcome.SourceMissingPath}");
        if (outcome.ErrorMessage is not null)
            return InternalServerErrorResponse($"OCR failed: {outcome.ErrorMessage}");

        SubtitleTrack track = outcome.Track!;
        return Ok(
            new
            {
                language = track.Language,
                stream_index = body.StreamIndex,
                target_format = format.ToString().ToLowerInvariant(),
                cue_count = track.CueCount,
                file_path = track.FilePath,
            }
        );
    }

    // ─── Whisper ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs whisper.cpp transcription on an audio stream of a VideoFile.
    /// Progress is broadcast via <c>ContentAnalysisHub</c> (event
    /// <c>WhisperProgress</c>). Heavy — owner-only.
    /// </summary>
    [HttpPost("whisper/{videoFileId}")]
    public async Task<IActionResult> Whisper(
        string videoFileId,
        [FromBody] WhisperRequest body,
        CancellationToken ct
    )
    {
        if (whisperTranscriber is null)
            return NotImplementedResponse("Whisper transcriber is not registered on this build");

        if (!Ulid.TryParse(videoFileId, out Ulid fileId))
            return BadRequestResponse("Invalid video file id");

        if (string.IsNullOrWhiteSpace(body.Language))
            return BadRequestResponse("language is required");

        if (
            !Enum.TryParse(
                body.Model ?? "LargeV3",
                ignoreCase: true,
                out WhisperModelSize modelSize
            )
        )
            return BadRequestResponse(
                $"Unknown model '{body.Model}'. Valid values: {string.Join(", ", Enum.GetNames<WhisperModelSize>())}"
            );

        SignalRProgressObserver observer = new(hubContext, videoFileId);

        ContentAnalysisService.WhisperOutcome outcome =
            await contentAnalysisService.TranscribeAsync(
                fileId,
                body.AudioStreamIndex,
                body.Language,
                modelSize,
                body.TranslateToEnglish,
                observer,
                ct
            );

        if (outcome.NotFound)
            return NotFoundResponse("Video file not found");
        if (outcome.SourceMissingPath is not null)
            return NotFoundResponse($"Source file missing on disk: {outcome.SourceMissingPath}");
        if (outcome.ErrorMessage is not null)
            return InternalServerErrorResponse($"Transcription failed: {outcome.ErrorMessage}");

        SubtitleTrack track = outcome.Track!;
        return Ok(
            new
            {
                language = body.Language,
                audio_stream_index = body.AudioStreamIndex,
                translate_to_english = body.TranslateToEnglish,
                model = modelSize.ToString(),
                file_path = track.FilePath,
                cue_count = track.CueCount,
            }
        );
    }

    // ─── Manual segment edit ─────────────────────────────────────────────────

    /// <summary>
    /// Updates the start/end of a content segment and marks it as manually
    /// edited (<c>source="manual"</c>). The auto-detector will skip episodes
    /// that already have a manual row of the same segment type.
    /// </summary>
    [HttpPut("segments/{segmentId}")]
    public async Task<IActionResult> EditSegment(
        string segmentId,
        [FromBody] EditSegmentRequest body
    )
    {
        if (!Ulid.TryParse(segmentId, out Ulid id))
            return BadRequestResponse("Invalid segment id");

        if (body.StartSeconds < 0 || body.EndSeconds < 0)
            return BadRequestResponse("start_seconds and end_seconds must not be negative");
        if (body.EndSeconds <= body.StartSeconds)
            return BadRequestResponse("end_seconds must be greater than start_seconds");

        ContentSegment? segment = await contentSegmentRepository.UpdateAsync(
            id,
            s =>
            {
                s.StartSeconds = body.StartSeconds;
                s.EndSeconds = body.EndSeconds;
                s.Source = "manual";
            }
        );

        if (segment is null)
            return NotFoundResponse("Content segment not found");

        return Ok(
            new
            {
                id = segment.Id,
                episode_id = segment.EpisodeId,
                movie_id = segment.MovieId,
                segment_type = segment.SegmentType.ToString().ToLowerInvariant(),
                start_seconds = segment.StartSeconds,
                end_seconds = segment.EndSeconds,
                source = segment.Source,
                confidence = segment.Confidence,
            }
        );
    }
}
