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

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Encoder.Progress;
using NoMercy.Resources;
using NoMercy.Storage;

namespace NoMercy.Encoder.ContentAnalysis;

/// <summary>
/// Detects black-bar cropping by running FFmpeg's <c>cropdetect</c> filter on a
/// short sample near the middle of the input and picking the most frequent
/// <c>crop=W:H:X:Y</c> line from stderr.
///
/// The middle of the file is sampled (not the opening logo or credits) so a
/// single black frame at the start does not skew the detection.
/// </summary>
public partial class CropDetector(
    EncoderOptions options,
    IProcessRunner processRunner,
    IStorage storage,
    ILogger<CropDetector> logger,
    IAnalysisProgressObserver? progress = null
) : ICropDetector
{
    // Minimum number of observations before we trust a crop value. Protects
    // against one-off black frames producing spurious crops.
    private const int MinObservations = 5;

    // Sample window per spec — 180s starting 60s into the file. Shorter
    // windows (the previous 60s) misclassified scope-aspect content whose
    // letterbox settled only after long fade-ins.
    private const int StartOffsetSeconds = 60;
    private static readonly TimeSpan ScanDuration = TimeSpan.FromSeconds(180);

    // round=4 forces detected crop dimensions to multiples of 4. Multiples
    // of 2 (the previous default) tripped a handful of HEVC encoders that
    // require modulo-4 — and the visual difference between mod2 and mod4
    // crops is imperceptible.
    //
    // The black-bar threshold (cropdetect limit) is transfer-dependent. SDR
    // black sits at code value 16 (or 0 for full-range), so limit=24 catches
    // it. HDR/PQ (smpte2084) and HLG (arib-std-b67) map "black" to a much
    // higher code value plus mastering noise, so limit=24 sees the whole
    // frame as picture and reports NO crop — baking the letterbox into a
    // stream-copy. HDR sources therefore need a higher limit. Measured on a
    // real 2160p HDR10 source: limit=24 → crop=3840:2160:0:0 (wrong),
    // limit=128 → crop=3616:1608:224:276 (the actual scope frame).
    private const int SdrCropDetectLimit = 24;
    private const int HdrCropDetectLimit = 128;

    private static string CropDetectFilter(bool sourceIsHdr) =>
        $"cropdetect=limit={(sourceIsHdr ? HdrCropDetectLimit : SdrCropDetectLimit)}:round=4:reset=0";

    private static readonly HashSet<string> HdrTransfers = ["smpte2084", "arib-std-b67"];

    public Task<CropResult> DetectAsync(string inputPath, CancellationToken ct) =>
        DetectAsync(inputPath, sourceVideoFileId: null, sourceIsHdr: false, ct);

    public Task<CropResult> DetectAsync(
        string inputPath,
        Guid? sourceVideoFileId,
        CancellationToken ct
    ) => DetectAsync(inputPath, sourceVideoFileId, sourceIsHdr: false, ct);

    public async Task<CropResult> DetectAsync(
        string inputPath,
        Guid? sourceVideoFileId,
        bool? sourceIsHdr,
        CancellationToken ct
    )
    {
        await using LocalPathLease inputLease = storage.AcquireLocalPath(inputPath);

        // Callers that already analysed the source (PlanStage) pass the known
        // HDR flag so no extra probe runs. Callers that don't (the on-demand
        // content-analysis API) pass null and we probe the transfer here so
        // the limit is still transfer-correct instead of silently SDR.
        bool isHdr =
            sourceIsHdr ?? await ProbeIsHdrAsync(inputLease.Path, ct).ConfigureAwait(false);

        string[] args =
        [
            "-hide_banner",
            // Ahead of -i, or it lands on the output and the decoder — the whole
            // cost of a cropdetect scan — runs on every core the host has.
            "-threads",
            EncodeThreadBudget.AuxiliaryPass.ToString(),
            "-ss",
            StartOffsetSeconds.ToString(),
            "-i",
            inputLease.Path,
            "-t",
            ((int)ScanDuration.TotalSeconds).ToString(),
            "-vf",
            CropDetectFilter(isHdr),
            "-filter_threads",
            EncodeThreadBudget.AuxiliaryPass.ToString(),
            "-f",
            "null",
            "-",
        ];

        Dictionary<string, int> observations = [];

        string jobId = sourceVideoFileId?.ToString("N") ?? Guid.NewGuid().ToString("N");
        IAnalysisProgressObserver observer = progress ?? NullAnalysisProgressObserver.Instance;
        observer.Report(jobId, "crop", 0, "scanning");

        ProcessResult result = await processRunner.RunAsync(
            options.FfmpegPath,
            args,
            onStdOut: null,
            onStdErr: line =>
            {
                Match match = CropRegex().Match(line);
                if (!match.Success)
                    return;

                string crop = match.Groups["crop"].Value;
                observations[crop] = observations.GetValueOrDefault(crop) + 1;
            },
            workingDirectory: null,
            cancellationToken: ct
        );

        observer.Report(jobId, "crop", 100, "done");

        int totalObservations = observations.Values.Sum();

        if (!result.IsSuccess)
        {
            logger.LogWarning(
                "cropdetect returned exit code {ExitCode}. Skipping crop.",
                result.ExitCode
            );
            return new(
                0,
                0,
                0,
                0,
                ShouldCrop: false,
                SourceVideoFileId: sourceVideoFileId,
                SampleFramesAnalyzed: totalObservations,
                Confidence: 0
            );
        }

        if (observations.Count == 0)
        {
            logger.LogDebug("cropdetect observed no crop values for {Input}", inputPath);
            return new(
                0,
                0,
                0,
                0,
                ShouldCrop: false,
                SourceVideoFileId: sourceVideoFileId,
                SampleFramesAnalyzed: 0,
                Confidence: 0
            );
        }

        KeyValuePair<string, int> top = observations.OrderByDescending(kv => kv.Value).First();
        string bestCrop = top.Key;
        int count = top.Value;

        double confidence = totalObservations > 0 ? (double)count / totalObservations : 0;

        if (count < MinObservations)
        {
            logger.LogDebug(
                "cropdetect top result {Crop} has only {Count} observations; not cropping",
                bestCrop,
                count
            );
            return new(
                0,
                0,
                0,
                0,
                ShouldCrop: false,
                SourceVideoFileId: sourceVideoFileId,
                SampleFramesAnalyzed: count,
                Confidence: confidence
            );
        }

        // crop=W:H:X:Y — parse the four parts.
        string[] parts = bestCrop.Split(':');
        if (parts.Length != 4)
            return new(
                0,
                0,
                0,
                0,
                ShouldCrop: false,
                SourceVideoFileId: sourceVideoFileId,
                SampleFramesAnalyzed: count,
                Confidence: confidence
            );

        if (
            !int.TryParse(parts[0], out int width)
            || !int.TryParse(parts[1], out int height)
            || !int.TryParse(parts[2], out int x)
            || !int.TryParse(parts[3], out int y)
        )
        {
            return new(
                0,
                0,
                0,
                0,
                ShouldCrop: false,
                SourceVideoFileId: sourceVideoFileId,
                SampleFramesAnalyzed: count,
                Confidence: confidence
            );
        }

        logger.LogInformation(
            "cropdetect → {Width}x{Height} at ({X},{Y}) ({Count} obs, confidence {Confidence:P0})",
            width,
            height,
            x,
            y,
            count,
            confidence
        );

        // Do not crop when the detected rectangle matches the full frame.
        bool shouldCrop = x > 0 || y > 0;

        return new(
            width,
            height,
            x,
            y,
            ShouldCrop: shouldCrop,
            SourceVideoFileId: sourceVideoFileId,
            SampleFramesAnalyzed: count,
            Confidence: confidence
        );
    }

    /// <summary>
    /// Probes the first video stream's colour transfer to decide whether the
    /// source is HDR (PQ / HLG). Best-effort: any failure returns false so the
    /// detector falls back to the SDR limit rather than failing the analysis.
    /// </summary>
    private async Task<bool> ProbeIsHdrAsync(string localPath, CancellationToken ct)
    {
        try
        {
            string[] args =
            [
                "-v",
                "error",
                "-select_streams",
                "v:0",
                "-show_entries",
                "stream=color_transfer",
                "-of",
                "default=noprint_wrappers=1:nokey=1",
                localPath,
            ];

            ProcessResult probe = await processRunner
                .RunAsync(options.FfprobePath, args, workingDirectory: null, cancellationToken: ct)
                .ConfigureAwait(false);

            if (!probe.IsSuccess)
                return false;

            string transfer = probe.StdOut.Trim().ToLowerInvariant();
            return HdrTransfers.Contains(transfer);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(
                ex,
                "HDR transfer probe failed for {Input}; assuming SDR crop threshold",
                localPath
            );
            return false;
        }
    }

    [GeneratedRegex(
        @"crop=(?<crop>\d+:\d+:\d+:\d+)",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex CropRegex();
}
