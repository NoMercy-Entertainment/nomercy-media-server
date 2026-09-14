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
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.TvShows;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.ContentAnalysis;
using NoMercy.Encoder.ContentAnalysis.Fingerprinting;
using NoMercy.Encoder.Progress;
using NoMercy.Encoder.Subtitles;
using NoMercy.Storage;
using ContentSegment = NoMercy.Database.Models.Media.ContentSegment;
using ContentSegmentType = NoMercy.Database.Models.Media.ContentSegmentType;
using VideoFile = NoMercy.Database.Models.Media.VideoFile;

namespace NoMercy.Data.Services;

/// <summary>
/// Business logic extracted from
/// <see cref="NoMercy.Api.Controllers.V1.Encoder.EncoderContentAnalysisController"/>
/// and its deprecated dashboard alias. Both controllers own auth, HTTP input
/// binding, and JSON response shaping; this service owns file resolution,
/// invoking the ffmpeg/whisper/tesseract detectors, and (for the encoder
/// route) persisting detected segments.
/// </summary>
public class ContentAnalysisService(
    ICropDetector cropDetector,
    IAudioFingerprinter fingerprinter,
    IIntroDetector introDetector,
    ISubtitleOcrEngine? ocrEngine,
    IWhisperTranscriber? whisperTranscriber,
    IVideoFileRepository videoFileRepository,
    IContentSegmentRepository contentSegmentRepository,
    IStorageDriver storageDriver
)
{
    private const int MinEpisodesForDetection = 2;
    private static readonly TimeSpan IntroOutroWindow = TimeSpan.FromMinutes(3);

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
    // Season intro / outro fingerprinting
    // -------------------------------------------------------------------------

    public sealed record SeasonIntroOutcome
    {
        public int EpisodesFound { get; init; }
        public bool NotEnoughEpisodes { get; init; }
        public int EpisodesScanned { get; init; }
        public bool NotEnoughFingerprints { get; init; }
        public string? FingerprintError { get; init; }
        public IntroMarker? IntroMarker { get; init; }
        public IntroMarker? OutroMarker { get; init; }
        public string? IntroFingerprintHash { get; init; }
        public string? OutroFingerprintHash { get; init; }
    }

    /// <summary>
    /// Fingerprints every encoded episode in a season and detects a shared
    /// intro/outro. When <paramref name="persistToDatabase"/> is set, detected
    /// markers are written as <c>Source="auto"</c> <see cref="ContentSegment"/>
    /// rows, skipping episodes that already carry a manual override of the
    /// same segment type.
    /// </summary>
    public async Task<SeasonIntroOutcome> DetectIntroForSeasonAsync(
        int seasonId,
        bool persistToDatabase,
        CancellationToken ct
    )
    {
        List<Episode> episodes = await videoFileRepository.GetEncodedEpisodesForSeasonAsync(
            seasonId,
            ct
        );

        if (episodes.Count < MinEpisodesForDetection)
            return new() { EpisodesFound = episodes.Count, NotEnoughEpisodes = true };

        List<(Episode Episode, AudioFingerprint Intro, AudioFingerprint Outro)> prints = [];

        foreach (Episode episode in episodes)
        {
            ct.ThrowIfCancellationRequested();
            VideoFile? source = episode.VideoFiles.FirstOrDefault();
            if (source is null)
                continue;

            string path = storageDriver.CombinePath(source.HostFolder, source.Filename);
            if (!storageDriver.FileExists(path))
                continue;

            try
            {
                AudioFingerprint introPrint = await fingerprinter.FingerprintAsync(
                    path,
                    new(TimeSpan.Zero, IntroOutroWindow),
                    ct
                );

                TimeSpan duration = ParseDuration(source.Duration);
                TimeSpan outroStart = ResolveOutroStart(duration, IntroOutroWindow);

                AudioFingerprint outroPrint = await fingerprinter.FingerprintAsync(
                    path,
                    new(outroStart, IntroOutroWindow),
                    ct
                );

                prints.Add((episode, introPrint, outroPrint));
            }
            catch (Exception ex)
            {
                return new()
                {
                    EpisodesFound = episodes.Count,
                    FingerprintError =
                        $"Fingerprinting failed for episode {episode.Id}: {ex.Message}",
                };
            }
        }

        if (prints.Count < MinEpisodesForDetection)
            return new() { EpisodesFound = episodes.Count, NotEnoughFingerprints = true };

        List<AudioFingerprint> introFingerprints = [.. prints.Select(t => t.Intro)];
        List<AudioFingerprint> outroFingerprints = [.. prints.Select(t => t.Outro)];

        IntroMarker? introMarker = introDetector.DetectIntro(introFingerprints);
        IntroMarker? outroMarker = introDetector.DetectOutro(outroFingerprints);

        if (persistToDatabase)
            await PersistDetectedSegmentsAsync(prints, introMarker, outroMarker);

        return new()
        {
            EpisodesFound = episodes.Count,
            EpisodesScanned = prints.Count,
            IntroMarker = introMarker,
            OutroMarker = outroMarker,
            IntroFingerprintHash = introMarker is not null
                ? ComputeFingerprintHash(introFingerprints)
                : null,
            OutroFingerprintHash = outroMarker is not null
                ? ComputeFingerprintHash(outroFingerprints)
                : null,
        };
    }

    private async Task PersistDetectedSegmentsAsync(
        List<(Episode Episode, AudioFingerprint Intro, AudioFingerprint Outro)> prints,
        IntroMarker? introMarker,
        IntroMarker? outroMarker
    )
    {
        if (introMarker is null && outroMarker is null)
            return;

        List<int> episodeIds = [.. prints.Select(t => t.Episode.Id)];

        List<ContentSegment> existingManual =
            await contentSegmentRepository.GetForEpisodesBySourceAsync(episodeIds, "manual");
        HashSet<(int EpisodeId, ContentSegmentType Type)> manualKeys =
        [
            .. existingManual.Select(cs => (cs.EpisodeId!.Value, cs.SegmentType)),
        ];

        List<ContentSegment> toUpsert = [];

        foreach ((Episode episode, _, _) in prints)
        {
            if (
                introMarker is not null
                && !manualKeys.Contains((episode.Id, ContentSegmentType.Intro))
            )
                toUpsert.Add(BuildSegment(episode.Id, ContentSegmentType.Intro, introMarker));

            if (
                outroMarker is not null
                && !manualKeys.Contains((episode.Id, ContentSegmentType.Outro))
            )
                toUpsert.Add(BuildSegment(episode.Id, ContentSegmentType.Outro, outroMarker));
        }

        if (toUpsert.Count > 0)
            await contentSegmentRepository.ReplaceSegmentsForEpisodesAsync(
                episodeIds,
                "auto",
                toUpsert
            );
    }

    private static ContentSegment BuildSegment(
        int episodeId,
        ContentSegmentType type,
        IntroMarker marker
    ) =>
        new()
        {
            EpisodeId = episodeId,
            SegmentType = type,
            StartSeconds = marker.Start.TotalSeconds,
            EndSeconds = marker.End.TotalSeconds,
            Confidence = marker.Confidence,
            Source = "auto",
        };

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

        WhisperOptions options = new(
            ModelPath: string.Empty,
            ModelSize: modelSize,
            TranslateToEnglish: translateToEnglish
        );

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

    /// <summary>VideoFile.Duration is stored as "HH:MM:SS"; malformed values scan as zero.</summary>
    internal static TimeSpan ParseDuration(string? raw) =>
        raw is not null && TimeSpan.TryParse(raw, out TimeSpan parsed) ? parsed : TimeSpan.Zero;

    /// <summary>
    /// Picks the outro scan window: the last <paramref name="window"/> of the
    /// source, or the whole file when it is shorter than one window.
    /// </summary>
    internal static TimeSpan ResolveOutroStart(TimeSpan duration, TimeSpan window) =>
        duration > window ? duration - window : TimeSpan.Zero;

    /// <summary>
    /// Stable MD5-based hex digest of the XOR-reduced fingerprint hashes,
    /// used as the <c>fingerprint_hash</c> field in content_segments rows.
    /// Not cryptographic — just a cheap stable identifier.
    /// </summary>
    internal static string ComputeFingerprintHash(IReadOnlyList<AudioFingerprint> prints)
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
