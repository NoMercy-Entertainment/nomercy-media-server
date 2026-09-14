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
using NoMercy.Data.Services;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.ContentAnalysis;
using NoMercy.Encoder.Subtitles;

namespace NoMercy.Api.Controllers.V1.Dashboard.Analysis;

/// <summary>
/// On-demand content-analysis probes. Useful when dialing in profiles
/// (does this source actually have letterbox bars?) or debugging the
/// auto-detection pipeline without kicking off a full encode.
/// </summary>
[ApiController]
[Tags("Dashboard Content Analysis")]
[ApiVersion(1.0)]
[Authorize(Policy = "Owner")]
[Route("api/v{version:apiVersion}/dashboard/content-analysis")]
public class ContentAnalysisController(
    ContentAnalysisService contentAnalysisService,
    ISubtitleOcrEngine? ocrEngine,
    IWhisperTranscriber? whisperTranscriber
) : BaseController
{
    /// <summary>
    /// Runs the crop detector against a VideoFile by id and returns the
    /// detected rectangle (or <c>should_crop=false</c> if the frame is
    /// already letterbox-free). Ffmpeg-bound — can take up to 60 seconds
    /// on large sources. Owner-only to avoid DoS-by-probe.
    /// </summary>
    /// <remarks>
    /// <b>Deprecated.</b> Use <c>POST /api/v1/encoder/content-analysis/crop/{videoFileId}</c>
    /// instead. This alias will be removed in a future release.
    /// </remarks>
    [HttpGet("crop/{videoFileId}")]
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

    /// <summary>
    /// Runs subtitle OCR on a single bitmap subtitle stream (PGS / VobSub /
    /// DVB) inside the requested VideoFile and returns the resulting
    /// WebVTT track. Useful for spot-checking OCR quality before enabling
    /// it on a library-wide re-encode.
    /// </summary>
    [HttpPost("ocr/{videoFileId}")]
    public async Task<IActionResult> OcrBitmapSubtitle(
        string videoFileId,
        [FromQuery] int streamIndex,
        [FromQuery] string language,
        CancellationToken ct
    )
    {
        if (ocrEngine is null)
            return NotImplementedResponse("Subtitle OCR engine is not registered on this build");

        if (!Ulid.TryParse(videoFileId, out Ulid fileId))
            return BadRequestResponse("Invalid video file id");

        if (string.IsNullOrWhiteSpace(language))
            return BadRequestResponse("language query parameter is required");

        ContentAnalysisService.OcrOutcome outcome =
            await contentAnalysisService.OcrBitmapSubtitleAsync(
                fileId,
                streamIndex,
                language,
                SubtitleCodecType.WebVtt,
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
                language,
                stream_index = streamIndex,
                cue_count = track.CueCount,
                file_path = track.FilePath,
            }
        );
    }

    /// <summary>
    /// Runs whisper.cpp against the first audio stream of a VideoFile and
    /// writes the resulting WebVTT next to the source. Heavy — whisper is
    /// multi-minute work even on decent hardware — so owner-only and
    /// intended for dashboard spot-checks of transcription quality, not
    /// library-wide jobs (the encode pipeline handles those).
    /// </summary>
    [HttpPost("transcribe/{videoFileId}")]
    public async Task<IActionResult> Transcribe(
        string videoFileId,
        [FromQuery] string language,
        [FromQuery] bool translateToEnglish = false,
        CancellationToken ct = default
    )
    {
        if (whisperTranscriber is null)
            return NotImplementedResponse("Whisper transcriber is not registered on this build");

        if (!Ulid.TryParse(videoFileId, out Ulid fileId))
            return BadRequestResponse("Invalid video file id");

        if (string.IsNullOrWhiteSpace(language))
            return BadRequestResponse("language query parameter is required");

        ContentAnalysisService.WhisperOutcome outcome =
            await contentAnalysisService.TranscribeAsync(
                fileId,
                audioStreamIndex: 0,
                language: language,
                modelSize: WhisperModelSize.LargeV3,
                translateToEnglish: translateToEnglish,
                progress: null,
                ct: ct
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
                language,
                translate_to_english = translateToEnglish,
                file_path = track.FilePath,
                cue_count = track.CueCount,
            }
        );
    }

    /// <summary>
    /// Runs the chromaprint intro / outro detector against every episode in
    /// a season that has at least one VideoFile on disk and returns the
    /// detected marker ranges. Useful for validating chromaprint quality on
    /// a specific season before trusting the auto-detection subscriber's
    /// output. Owner-only — fingerprinting every episode is minutes of
    /// ffmpeg work per episode.
    /// </summary>
    [HttpPost("intro/{seasonId:int}")]
    public async Task<IActionResult> DetectIntroForSeason(int seasonId, CancellationToken ct)
    {
        ContentAnalysisService.SeasonIntroOutcome outcome =
            await contentAnalysisService.DetectIntroForSeasonAsync(
                seasonId,
                persistToDatabase: false,
                ct
            );

        if (outcome.NotEnoughEpisodes)
            return BadRequestResponse(
                $"Need at least 2 encoded episodes, season has {outcome.EpisodesFound}"
            );
        if (outcome.FingerprintError is not null)
            return InternalServerErrorResponse(outcome.FingerprintError);
        if (outcome.NotEnoughFingerprints)
            return BadRequestResponse("Not enough successful fingerprints to compare");

        return Ok(
            new
            {
                season_id = seasonId,
                episodes_scanned = outcome.EpisodesScanned,
                intro = outcome.IntroMarker is null
                    ? null
                    : new
                    {
                        start_seconds = outcome.IntroMarker.Start.TotalSeconds,
                        end_seconds = outcome.IntroMarker.End.TotalSeconds,
                        confidence = outcome.IntroMarker.Confidence,
                    },
                outro = outcome.OutroMarker is null
                    ? null
                    : new
                    {
                        start_seconds = outcome.OutroMarker.Start.TotalSeconds,
                        end_seconds = outcome.OutroMarker.End.TotalSeconds,
                        confidence = outcome.OutroMarker.Confidence,
                    },
            }
        );
    }
}
