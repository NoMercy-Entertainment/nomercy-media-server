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
using Microsoft.Extensions.Logging;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Encoder.Progress;
using NoMercy.NmSystem.Monitoring;
using NoMercy.Storage;

namespace NoMercy.Encoder.ContentAnalysis.Fingerprinting;

/// <summary>
/// FFmpeg-backed fingerprinter. Invokes ffmpeg with the chromaprint muxer
/// and decodes its output back into the raw hashes. The muxer has no text
/// format: <c>raw</c> writes the hashes as binary 32-bit integers and
/// <c>compressed</c> as binary bytes, neither of which survives being read
/// as stdout text. <c>base64</c> is the one text-safe form, so it is
/// requested here (as the AcoustID fingerprinter in MediaProcessing does)
/// and <see cref="ChromaprintFingerprintDecoder"/> unpacks it.
///
/// chromaprint advances one hash per <see cref="AudioFingerprint.ChromaprintHopSamples"/>
/// samples at its internal <see cref="AudioFingerprint.ChromaprintSampleRate"/> Hz
/// mono rate, so FrameDuration is the constant
/// <see cref="AudioFingerprint.ChromaprintFrameDuration"/> rather than a value
/// read back from ffmpeg.
///
/// This is the fire-and-forget path both <c>IntroDetectSubscriber</c> and
/// <c>IntroDetectionSubscriber</c> funnel through, and the worst NAS-read
/// offender among background jobs — each call reads up to a few minutes of
/// audio per episode. It yields to active playback via
/// <see cref="MediaActivityMonitor.WaitForIdleAsync"/> before touching the
/// source file, so it never competes with a streaming segment serve for the
/// same NFS mount.
/// </summary>
public class ChromaprintFingerprinter(
    EncoderOptions options,
    IProcessRunner processRunner,
    IStorage storage,
    ILogger<ChromaprintFingerprinter> logger,
    MediaActivityMonitor activityMonitor,
    IAnalysisProgressObserver? progress = null
) : IAudioFingerprinter
{
    private static readonly TimeSpan MaxIdleWait = TimeSpan.FromMinutes(5);

    public async Task<AudioFingerprint> FingerprintAsync(
        string filePath,
        FingerprintWindow? window,
        CancellationToken ct
    )
    {
        string jobId = Guid.NewGuid().ToString("N");
        IAnalysisProgressObserver observer = progress ?? NullAnalysisProgressObserver.Instance;
        observer.Report(jobId, "fingerprint", 0, "fingerprinting");

        // Defer to active playback (never longer than MaxIdleWait) before
        // reading from the source file — see MediaActivityMonitor.
        await activityMonitor.WaitForIdleAsync(MaxIdleWait, ct).ConfigureAwait(false);

        await using LocalPathLease inputLease = storage.AcquireLocalPath(filePath);
        List<string> args = ["-v", "error", "-nostdin"];

        if (window is not null)
        {
            double startSeconds = window.Start.TotalSeconds;

            // A negative Start means "relative to the end of the file" (e.g. the
            // outro window). -ss only accepts a non-negative offset from the
            // start — the end-relative case needs -sseof, which takes the same
            // negative value ffmpeg subtracts from the input duration.
            if (startSeconds < 0)
            {
                args.Add("-sseof");
                args.Add(startSeconds.ToString("F3", CultureInfo.InvariantCulture));
            }
            else
            {
                args.Add("-ss");
                args.Add(startSeconds.ToString("F3", CultureInfo.InvariantCulture));
            }

            args.Add("-t");
            args.Add(window.Duration.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture));
        }

        // Drop video, downmix to mono at chromaprint's preferred sample rate.
        args.AddRange([
            "-i",
            inputLease.Path,
            "-vn",
            "-sn",
            "-dn",
            "-ac",
            "1",
            "-ar",
            AudioFingerprint.ChromaprintSampleRate.ToString(CultureInfo.InvariantCulture),
            "-f",
            "chromaprint",
            "-fp_format",
            "base64",
            "-",
        ]);

        ProcessResult result = await processRunner.RunAsync(
            options.FfmpegPath,
            [.. args],
            workingDirectory: null,
            cancellationToken: ct
        );

        if (!result.IsSuccess)
        {
            logger.LogWarning(
                "chromaprint fingerprinting failed for {Path} (exit {Exit}): {Stderr}",
                filePath,
                result.ExitCode,
                result.StdErr
            );
            observer.Report(jobId, "fingerprint", 100, "failed");
            return Empty(window);
        }

        uint[] hashes = ChromaprintFingerprintDecoder.DecodeBase64(result.StdOut);
        TimeSpan startTime = window?.Start ?? TimeSpan.Zero;

        observer.Report(jobId, "fingerprint", 100, "done");
        return new(hashes, AudioFingerprint.ChromaprintFrameDuration, startTime);
    }

    private static AudioFingerprint Empty(FingerprintWindow? window) =>
        new([], AudioFingerprint.ChromaprintFrameDuration, window?.Start ?? TimeSpan.Zero);
}
