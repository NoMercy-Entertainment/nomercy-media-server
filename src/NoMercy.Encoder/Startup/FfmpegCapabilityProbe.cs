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

using Microsoft.Extensions.Logging;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Errors;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.Infrastructure;
using NoMercy.NmSystem.Information;
using NoMercy.Storage;

namespace NoMercy.Encoder.Startup;

/// <summary>
/// Probes optional runtime dependencies and validates the installed FFmpeg
/// build. Reuses <see cref="IFfmpegCapabilities"/> for filters/protocols so
/// those shell calls are not duplicated. Muxers require a separate probe
/// because <see cref="IFfmpegCapabilities"/> does not expose them.
/// </summary>
public sealed class FfmpegCapabilityProbe(
    IFfmpegCapabilities ffmpegCapabilities,
    IProcessRunner processRunner,
    IStorage storage,
    EncoderOptions options,
    ILogger<FfmpegCapabilityProbe> logger
) : IFfmpegCapabilityProbe
{
    private static readonly string[] RequiredFilters =
    [
        "libplacebo",
        "tonemap_opencl",
        "zscale",
        "cropdetect",
        "ass",
        "overlay",
        "hwupload",
        "hwdownload",
    ];

    private static readonly string[] RequiredMuxers =
    [
        "hls",
        "dash",
        "mp4",
        "matroska",
        "mp3",
        "flac",
        "ogg",
    ];

    private CapabilityReport? _cached;

    public CapabilityReport? GetCachedReport() => _cached;

    public async Task ProbeAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Running FFmpeg capability probe");

        // FfmpegCapabilities already probed during HardwareInitializationService —
        // call ProbeAsync only if the sets are empty (e.g. standalone usage).
        if (ffmpegCapabilities.AvailableEncoders.Count == 0)
            await ffmpegCapabilities.ProbeAsync(ct).ConfigureAwait(false);

        bool bluRay = ffmpegCapabilities.HasProtocol("bluray");
        // ffmpeg ≥ 6.1 dropped the dvdread:// protocol form in favour of the
        // dvdvideo demuxer (-f dvdvideo -i /path/to/disc). Both stock builds
        // with --enable-libdvdread and the NoMercy fork expose libdvdread
        // exclusively through that demuxer, so the demuxer's presence is
        // the canonical capability check.
        bool dvdRead = ffmpegCapabilities.HasDemuxer("dvdvideo");

        List<string> missingFilters =
        [
            .. RequiredFilters.Where(f => !ffmpegCapabilities.HasFilter(f)),
        ];

        HashSet<string> availableMuxers = await ProbeMuxersAsync(ct).ConfigureAwait(false);
        List<string> missingMuxers = [.. RequiredMuxers.Where(m => !availableMuxers.Contains(m))];

        // Chromaprint fingerprinting is compiled into the NoMercy ffmpeg fork
        // as the chromaprint muxer (-c:a chromaprint) — no separate fpcalc
        // binary. Presence of the muxer is the canonical capability check.
        bool fpcalcPresent = availableMuxers.Contains("chromaprint");

        bool whisperModelPresent = ProbeWhisperModel();

        bool stemsplitModelPresent = ProbeStemsplitModel();

        (bool tesseractPresent, string? tesseractDir) = ProbeTesseract();

        List<EncoderRule> issues = BuildIssues(
            fpcalcPresent,
            whisperModelPresent,
            stemsplitModelPresent,
            tesseractPresent
        );

        _cached = new(
            BluRayProtocol: bluRay,
            DvdReadProtocol: dvdRead,
            AvailableEncoders: [.. ffmpegCapabilities.AvailableEncoders],
            MissingFilters: missingFilters,
            MissingMuxers: missingMuxers,
            FpcalcPresent: fpcalcPresent,
            WhisperModelPresent: whisperModelPresent,
            StemsplitModelPresent: stemsplitModelPresent,
            TesseractEngTraineddataPresent: tesseractPresent,
            TesseractModelsDirectory: tesseractDir,
            Issues: issues
        );

        logger.LogInformation(
            "Capability probe complete — BluRay={BluRay}, DvdRead={DvdRead}, MissingFilters={FilterCount}, MissingMuxers={MuxerCount}, fpcalc={Fpcalc}, WhisperModel={Whisper}, StemsplitModel={Stemsplit}, Tesseract={Tesseract}",
            bluRay,
            dvdRead,
            missingFilters.Count,
            missingMuxers.Count,
            fpcalcPresent,
            whisperModelPresent,
            stemsplitModelPresent,
            tesseractPresent
        );
    }

    // -------------------------------------------------------------------------

    private async Task<HashSet<string>> ProbeMuxersAsync(CancellationToken ct)
    {
        ProcessResult result = await processRunner
            .RunAsync(AppFiles.FfmpegPath, ["-hide_banner", "-muxers"], null, ct)
            .ConfigureAwait(false);

        HashSet<string> available = [];
        foreach (string line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.TrimStart();
            // Muxer lines: " E mp4              MP4 (MPEG-4 Part 14)"
            if (trimmed.Length < 3 || trimmed[0] != 'E')
                continue;

            string[] parts = trimmed[1..]
                .TrimStart()
                .Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
                available.Add(parts[0]);
        }

        return available;
    }

    private bool ProbeWhisperModel()
    {
        string? modelPath = options.WhisperModelPath;
        if (string.IsNullOrWhiteSpace(modelPath))
            return false;

        try
        {
            return storage.Exists(modelPath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool ProbeStemsplitModel()
    {
        string? modelPath = options.StemsplitModelPath;
        if (string.IsNullOrWhiteSpace(modelPath))
            return false;

        try
        {
            return storage.Exists(modelPath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private (bool present, string? directory) ProbeTesseract()
    {
        string? dir = options.TesseractModelsDirectory;
        if (string.IsNullOrWhiteSpace(dir))
            return (false, null);

        string engPath = Path.Combine(dir, "eng.traineddata");
        try
        {
            return (storage.Exists(engPath), dir);
        }
        catch (Exception)
        {
            return (false, dir);
        }
    }

    private static List<EncoderRule> BuildIssues(
        bool fpcalcPresent,
        bool whisperModelPresent,
        bool stemsplitModelPresent,
        bool tesseractPresent
    )
    {
        List<EncoderRule> issues = [];

        if (!fpcalcPresent)
            issues.Add(
                new(
                    EncoderRuleId.CapabilityFpcalcMissing,
                    EncoderRuleSeverity.Warning,
                    "fpcalc",
                    "Chromaprint muxer is not compiled into the bundled ffmpeg fork — audio fingerprinting unavailable.",
                    "Reinstall NoMercy or rebuild the ffmpeg fork with --enable-chromaprint."
                )
            );

        if (!whisperModelPresent)
            issues.Add(
                new(
                    EncoderRuleId.CapabilityWhisperMissing,
                    EncoderRuleSeverity.Warning,
                    "WhisperModelPath",
                    "Whisper model file is missing or not configured.",
                    "Set EncoderOptions.WhisperModelPath to a valid ggml .bin model file."
                )
            );

        if (!stemsplitModelPresent)
            issues.Add(
                new(
                    EncoderRuleId.CapabilityStemsplitMissing,
                    EncoderRuleSeverity.Warning,
                    "StemsplitModelPath",
                    "Stemsplit model file is missing or not configured.",
                    "Set EncoderOptions.StemsplitModelPath to a valid stemsplit .gguf model file."
                )
            );

        if (!tesseractPresent)
            issues.Add(
                new(
                    EncoderRuleId.CapabilityTesseractModelMissing,
                    EncoderRuleSeverity.Warning,
                    "TesseractModelsDirectory",
                    "Tesseract eng.traineddata is missing or TesseractModelsDirectory is not configured.",
                    "Set EncoderOptions.TesseractModelsDirectory to a folder containing eng.traineddata."
                )
            );

        return issues;
    }
}
