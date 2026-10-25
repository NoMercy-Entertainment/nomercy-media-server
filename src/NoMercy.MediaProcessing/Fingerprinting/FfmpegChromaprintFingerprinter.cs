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
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Providers.AcoustId;
using NoMercy.Storage;

namespace NoMercy.MediaProcessing.Fingerprinting;

/// <summary>
/// <see cref="IAudioFingerprinter"/> for the AcoustID lookup path — the compressed
/// chromaprint string via the fork ffmpeg's built-in chromaprint muxer (no separate
/// fpcalc binary). Lives in MediaProcessing because Providers must not reference the
/// Encoder-layer process runner and ffmpeg path it needs.
/// </summary>
public sealed partial class FfmpegChromaprintFingerprinter(
    EncoderOptions options,
    IProcessRunner processRunner,
    IStorage storage,
    ILogger<FfmpegChromaprintFingerprinter> logger
) : IAudioFingerprinter
{
    private static readonly TimeSpan FingerprintTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// A chromaprint fingerprint in AcoustID's wire form: URL-safe base64. The
    /// muxer writes nothing else on stdout, so the whole payload is the value —
    /// this only strips whitespace and rejects anything that isn't base64.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9_\-=]+$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Base64FingerprintRegex();

    public async Task<AudioFingerprint?> FingerprintAsync(string filePath, CancellationToken ct)
    {
        await using LocalPathLease inputLease = storage.AcquireLocalPath(filePath);

        // fp_format=base64, not "compressed": AcoustID's fingerprint parameter
        // takes the base64 form, and the muxer emits the compressed form as raw
        // binary with no FINGERPRINT=/DURATION= labels at all — those labels are
        // fpcalc's output format, not ffmpeg's. Parsing for them meant the match
        // never succeeded, every track logged "produced no FINGERPRINT" with a
        // screenful of binary, and no untagged album could ever be identified.
        string[] arguments =
        [
            "-v",
            "error",
            "-nostdin",
            "-i",
            inputLease.Path,
            "-vn",
            "-sn",
            "-dn",
            "-ac",
            "1",
            "-ar",
            "11025",
            "-f",
            "chromaprint",
            "-fp_format",
            "base64",
            "-",
        ];

        // ffmpeg reading a stalled network mount never returns, and RunAsync waits on
        // the caller's token alone — so one unreadable file held the single import
        // worker for good and every later music import queued behind it forever.
        using CancellationTokenSource fingerprintCts =
            CancellationTokenSource.CreateLinkedTokenSource(ct);
        fingerprintCts.CancelAfter(FingerprintTimeout);

        ProcessResult result;
        try
        {
            result = await processRunner.RunAsync(
                options.FfmpegPath,
                arguments,
                workingDirectory: null,
                cancellationToken: fingerprintCts.Token
            );
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(
                "chromaprint fingerprinting timed out after {Seconds}s for {Path}",
                FingerprintTimeout.TotalSeconds,
                filePath
            );
            return null;
        }

        if (!result.IsSuccess)
        {
            logger.LogWarning(
                "chromaprint fingerprinting failed for {Path} (exit {Exit}): {Stderr}",
                filePath,
                result.ExitCode,
                result.StdErr
            );
            return null;
        }

        string fingerprint = result.StdOut.Trim();
        if (fingerprint.Length == 0 || !Base64FingerprintRegex().IsMatch(fingerprint))
        {
            logger.LogWarning(
                "chromaprint produced no usable fingerprint for {Path}; output: {Output}",
                filePath,
                Truncate(fingerprint, 200)
            );
            return null;
        }

        int durationSeconds = await ProbeDurationSecondsAsync(inputLease.Path, ct);
        if (durationSeconds <= 0)
        {
            // AcoustID matches on fingerprint AND duration; submitting 0 returns
            // no results, so a failed probe is a failed fingerprint.
            logger.LogWarning("Could not determine duration for {Path}", filePath);
            return null;
        }

        return new(fingerprint, durationSeconds);
    }

    /// <summary>
    /// Track length in whole seconds via ffprobe, or 0 when it cannot be read.
    /// The chromaprint muxer does not report duration, so it is probed separately.
    /// </summary>
    private async Task<int> ProbeDurationSecondsAsync(string localPath, CancellationToken ct)
    {
        string[] arguments =
        [
            "-v",
            "error",
            "-show_entries",
            "format=duration",
            "-of",
            "default=noprint_wrappers=1:nokey=1",
            localPath,
        ];

        using CancellationTokenSource probeCts = CancellationTokenSource.CreateLinkedTokenSource(
            ct
        );
        probeCts.CancelAfter(FingerprintTimeout);

        ProcessResult result;
        try
        {
            result = await processRunner.RunAsync(
                options.FfprobePath,
                arguments,
                workingDirectory: null,
                cancellationToken: probeCts.Token
            );
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("duration probe timed out for {Path}", localPath);
            return 0;
        }

        if (!result.IsSuccess)
            return 0;

        return double.TryParse(
            result.StdOut.Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double seconds
        )
            ? (int)Math.Round(seconds)
            : 0;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
