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

using System.Security.Cryptography;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using NoMercy.Api.Hubs;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.TvShows;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.ContentAnalysis;
using NoMercy.Encoder.ContentAnalysis.Fingerprinting;
using NoMercy.Encoder.Errors;
using NoMercy.Encoder.Progress;
using NoMercy.Encoder.Subtitles;
using NoMercy.Storage;
using ContentSegment = NoMercy.Database.Models.Media.ContentSegment;
using ContentSegmentType = NoMercy.Database.Models.Media.ContentSegmentType;

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
    ICropDetector cropDetector,
    ISubtitleOcrEngine? ocrEngine,
    IWhisperTranscriber? whisperTranscriber,
    IAudioFingerprinter fingerprinter,
    IIntroDetector introDetector,
    IDbContextFactory<MediaContext> contextFactory,
    IHubContext<ContentAnalysisHub> hubContext,
    IStorageDriver storageDriver
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

        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        VideoFile? file = await context.VideoFiles.FirstOrDefaultAsync(v => v.Id == fileId, ct);

        if (file is null)
            return NotFoundResponse("Video file not found");

        string path = storageDriver.CombinePath(file.HostFolder, file.Filename);
        if (!storageDriver.FileExists(path))
            return NotFoundResponse($"Source file missing on disk: {path}");

        Guid sourceVideoFileId = new(fileId.ToByteArray());

        try
        {
            CropResult result = await cropDetector.DetectAsync(
                path,
                sourceVideoFileId,
                sourceIsHdr: null,
                ct
            );
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
        catch (Exception ex)
        {
            return InternalServerErrorResponse($"Crop detection failed: {ex.Message}");
        }
    }

    // ─── Intro / outro fingerprinting ────────────────────────────────────────

    /// <summary>
    /// Runs the chromaprint intro/outro detector against every episode in a
    /// season and returns detected content segments. Detected segments are
    /// persisted to the <c>ContentSegments</c> table (source="auto") unless
    /// a manual row of the same type already exists for that episode.
    /// </summary>
    [HttpPost("intro/{seasonId:int}")]
    public async Task<IActionResult> DetectIntroForSeason(int seasonId, CancellationToken ct)
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        List<Episode> episodes = await context
            .Episodes.AsNoTracking()
            .Include(e => e.VideoFiles)
            .Where(e => e.SeasonId == seasonId && e.VideoFiles.Count > 0)
            .OrderBy(e => e.EpisodeNumber)
            .ToListAsync(ct);

        int showId = await context
            .Seasons.AsNoTracking()
            .Where(s => s.Id == seasonId)
            .Select(s => s.TvId)
            .FirstOrDefaultAsync(ct);

        if (episodes.Count < 2)
            return BadRequestResponse(
                $"Need at least 2 encoded episodes, season has {episodes.Count}"
            );

        // ── Fingerprinting pass ──────────────────────────────────────────────
        List<(Episode Episode, AudioFingerprint Intro, AudioFingerprint Outro)> prints = [];

        foreach (Episode ep in episodes)
        {
            ct.ThrowIfCancellationRequested();
            VideoFile? source = ep.VideoFiles.FirstOrDefault();
            if (source is null)
                continue;

            string path = storageDriver.CombinePath(source.HostFolder, source.Filename);
            if (!storageDriver.FileExists(path))
                continue;

            try
            {
                AudioFingerprint introPrint = await fingerprinter.FingerprintAsync(
                    path,
                    new(TimeSpan.Zero, TimeSpan.FromMinutes(3)),
                    ct
                );

                TimeSpan duration = TimeSpan.TryParse(source.Duration, out TimeSpan parsed)
                    ? parsed
                    : TimeSpan.Zero;
                TimeSpan outroStart =
                    duration > TimeSpan.FromMinutes(3)
                        ? duration - TimeSpan.FromMinutes(3)
                        : TimeSpan.Zero;

                AudioFingerprint outroPrint = await fingerprinter.FingerprintAsync(
                    path,
                    new(outroStart, TimeSpan.FromMinutes(3)),
                    ct
                );

                prints.Add((ep, introPrint, outroPrint));
            }
            catch (Exception ex)
            {
                return InternalServerErrorResponse(
                    $"Fingerprinting failed for episode {ep.Id}: {ex.Message}"
                );
            }
        }

        if (prints.Count < 2)
            return BadRequestResponse("Not enough successful fingerprints to compare");

        List<AudioFingerprint> introFingerprints = [.. prints.Select(t => t.Intro)];
        List<AudioFingerprint> outroFingerprints = [.. prints.Select(t => t.Outro)];

        IntroMarker? introMarker = introDetector.DetectIntro(introFingerprints);
        IntroMarker? outroMarker = introDetector.DetectOutro(outroFingerprints);

        // ── Persist detected segments ────────────────────────────────────────
        // Load existing manual rows once so the per-episode check is O(1).
        List<int> episodeIds = [.. prints.Select(t => t.Episode.Id)];

        List<ContentSegment> existingManual = await context
            .ContentSegments.Where(cs =>
                cs.EpisodeId != null
                && episodeIds.Contains(cs.EpisodeId.Value)
                && cs.Source == "manual"
            )
            .ToListAsync(ct);

        HashSet<(int EpisodeId, ContentSegmentType Type)> manualKeys =
        [
            .. existingManual.Select(cs => (cs.EpisodeId!.Value, cs.SegmentType)),
        ];

        List<ContentSegment> toUpsert = [];

        foreach ((Episode ep, _, _) in prints)
        {
            if (introMarker is not null && !manualKeys.Contains((ep.Id, ContentSegmentType.Intro)))
            {
                toUpsert.Add(
                    new()
                    {
                        EpisodeId = ep.Id,
                        SegmentType = ContentSegmentType.Intro,
                        StartSeconds = introMarker.Start.TotalSeconds,
                        EndSeconds = introMarker.End.TotalSeconds,
                        Confidence = introMarker.Confidence,
                        Source = "auto",
                    }
                );
            }

            if (outroMarker is not null && !manualKeys.Contains((ep.Id, ContentSegmentType.Outro)))
            {
                toUpsert.Add(
                    new()
                    {
                        EpisodeId = ep.Id,
                        SegmentType = ContentSegmentType.Outro,
                        StartSeconds = outroMarker.Start.TotalSeconds,
                        EndSeconds = outroMarker.End.TotalSeconds,
                        Confidence = outroMarker.Confidence,
                        Source = "auto",
                    }
                );
            }
        }

        if (toUpsert.Count > 0)
        {
            // Remove stale auto rows before inserting fresh ones.
            List<ContentSegment> staleAuto = await context
                .ContentSegments.Where(cs =>
                    cs.EpisodeId != null
                    && episodeIds.Contains(cs.EpisodeId.Value)
                    && cs.Source == "auto"
                )
                .ToListAsync(ct);

            context.ContentSegments.RemoveRange(staleAuto);
            await context.ContentSegments.AddRangeAsync(toUpsert, ct);
            await context.SaveChangesAsync(ct);
        }

        // ── Build response ───────────────────────────────────────────────────
        string? introHash = introMarker is not null ? FingerprintHash(introFingerprints) : null;
        string? outroHash = outroMarker is not null ? FingerprintHash(outroFingerprints) : null;

        List<object> contentSegments = [];

        if (introMarker is not null)
            contentSegments.Add(
                new
                {
                    show_id = showId,
                    season_id = seasonId,
                    type = "intro",
                    start_seconds = introMarker.Start.TotalSeconds,
                    end_seconds = introMarker.End.TotalSeconds,
                    confidence = introMarker.Confidence,
                    source = "auto",
                    fingerprint_hash = introHash,
                }
            );

        if (outroMarker is not null)
            contentSegments.Add(
                new
                {
                    show_id = showId,
                    season_id = seasonId,
                    type = "outro",
                    start_seconds = outroMarker.Start.TotalSeconds,
                    end_seconds = outroMarker.End.TotalSeconds,
                    confidence = outroMarker.Confidence,
                    source = "auto",
                    fingerprint_hash = outroHash,
                }
            );

        return Ok(
            new
            {
                show_id = showId,
                season_id = seasonId,
                episodes_scanned = prints.Count,
                content_segments = contentSegments,
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

        SubtitleCodecType format = ParseTargetFormat(body.TargetFormat);

        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        VideoFile? file = await context.VideoFiles.FirstOrDefaultAsync(v => v.Id == fileId, ct);

        if (file is null)
            return NotFoundResponse("Video file not found");

        string path = storageDriver.CombinePath(file.HostFolder, file.Filename);
        if (!storageDriver.FileExists(path))
            return NotFoundResponse($"Source file missing on disk: {path}");

        try
        {
            SubtitleTrack track = await ocrEngine.OcrAsync(
                path,
                body.StreamIndex,
                body.Language,
                format,
                ct
            );
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
        catch (Exception ex)
        {
            return InternalServerErrorResponse($"OCR failed: {ex.Message}");
        }
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

        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        VideoFile? file = await context.VideoFiles.FirstOrDefaultAsync(v => v.Id == fileId, ct);

        if (file is null)
            return NotFoundResponse("Video file not found");

        string path = storageDriver.CombinePath(file.HostFolder, file.Filename);
        if (!storageDriver.FileExists(path))
            return NotFoundResponse($"Source file missing on disk: {path}");

        WhisperOptions options = new(
            ModelPath: string.Empty,
            ModelSize: modelSize,
            TranslateToEnglish: body.TranslateToEnglish
        );

        SignalRProgressObserver observer = new(hubContext, videoFileId);

        try
        {
            SubtitleTrack track = await whisperTranscriber.TranscribeAsync(
                path,
                audioStreamIndex: body.AudioStreamIndex,
                language: body.Language,
                options: options,
                progress: observer,
                ct: ct
            );
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
        catch (Exception ex)
        {
            return InternalServerErrorResponse($"Transcription failed: {ex.Message}");
        }
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
        [FromBody] EditSegmentRequest body,
        CancellationToken ct
    )
    {
        if (!Ulid.TryParse(segmentId, out Ulid id))
            return BadRequestResponse("Invalid segment id");

        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        ContentSegment? segment = await context.ContentSegments.FirstOrDefaultAsync(
            cs => cs.Id == id,
            ct
        );

        if (segment is null)
            return NotFoundResponse("Content segment not found");

        segment.StartSeconds = body.StartSeconds;
        segment.EndSeconds = body.EndSeconds;
        segment.Source = "manual";
        segment.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(ct);

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

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static SubtitleCodecType ParseTargetFormat(string? raw) =>
        (raw ?? "webvtt").ToLowerInvariant() switch
        {
            "vtt" or "webvtt" => SubtitleCodecType.WebVtt,
            "ass" => SubtitleCodecType.Ass,
            "srt" => SubtitleCodecType.Srt,
            _ => SubtitleCodecType.WebVtt,
        };

    /// <summary>
    /// Stable MD5-based hex digest of the XOR-reduced fingerprint hashes,
    /// used as the <c>fingerprint_hash</c> field in content_segments rows.
    /// Not cryptographic — just a cheap stable identifier.
    /// </summary>
    private static string FingerprintHash(IReadOnlyList<AudioFingerprint> prints)
    {
        uint[] combined = [.. prints[0].Hashes];
        for (int i = 1; i < prints.Count; i++)
        {
            uint[] h = prints[i].Hashes;
            int len = Math.Min(combined.Length, h.Length);
            for (int j = 0; j < len; j++)
                combined[j] ^= h[j];
        }

        byte[] bytes = new byte[combined.Length * 4];
        Buffer.BlockCopy(combined, 0, bytes, 0, bytes.Length);

        byte[] hash = MD5.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

// ─── Request / response DTOs ─────────────────────────────────────────────────

// ─── SignalR progress adapter ─────────────────────────────────────────────────
