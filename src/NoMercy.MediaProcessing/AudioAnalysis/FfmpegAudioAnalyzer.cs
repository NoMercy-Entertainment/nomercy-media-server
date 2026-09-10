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
using NoMercy.Encoder.Infrastructure;
using NoMercy.Storage;

namespace NoMercy.MediaProcessing.AudioAnalysis;

/// <summary>
/// Measures a track with one pass of the fork ffmpeg. Lives in MediaProcessing
/// for the same reason the chromaprint fingerprinter does: it needs the
/// Encoder-layer process runner and ffmpeg path, which Providers must not
/// reference.
/// </summary>
public sealed class FfmpegAudioAnalyzer(
    EncoderOptions options,
    IProcessRunner processRunner,
    IStorage storage,
    ILogger<FfmpegAudioAnalyzer> logger
) : IAudioAnalyzer
{
    private static readonly TimeSpan AnalysisTimeout = TimeSpan.FromMinutes(10);

    // 3: beat grid and confidence read from beatdetect's own metadata (nomercy-ffmpeg v1.0.40).
    // 4: aspectralstats moved ahead of beatdetect. Downstream of it the frame
    //    carrying final=1 was lost on real MP3s, so every version 3 verdict
    //    fell back to the legacy tempo without a grid and has to be redone.
    public int Version => 4;

    public async Task<AudioAnalysisResult?> AnalyzeAsync(string filePath, CancellationToken ct)
    {
        await using LocalPathLease inputLease = storage.AcquireLocalPath(filePath);

        AudioAnalysisOutputParser parser = new();

        // One pass, every detector. beatdetect, keydetect and aspectralstats
        // answer through ametadata on stdout; silencedetect and loudnorm answer
        // on stderr.
        //
        // The order is load-bearing, and every rule below was measured.
        //
        // aspectralstats goes FIRST. It re-frames: it consumes its input in
        // fixed 2048-sample windows, and each window keeps the metadata of the
        // first frame it swallowed. Downstream of beatdetect it dropped the
        // frame carrying final=1 on real MP3s — a 200 s 128 BPM click encoded
        // with libmp3lame yielded 0 final frames with aspectralstats between
        // beatdetect and ametadata and 1 with it ahead — while the 20 s lavfi
        // fixture happened to align and hid it. It does not alter the signal:
        // beatdetect reading its output measures the same grid (128.06 BPM,
        // offset 467.8 ms, interval 468.53 ms in both orders).
        //
        // beatdetect and keydetect pass frames through untouched, so ONE
        // ametadata directly after them prints every key, the centroid that
        // rode along from aspectralstats included. Exactly one: two instances
        // printing to file=- hold independent buffers and splice each other's
        // lines mid-write (measured: "frame:48   pts:921600  pts_tect.final=0"),
        // which can cut a verdict in half.
        //
        // Everything that alters the signal comes LAST. loudnorm normalizes as
        // well as reporting, and reading tempo downstream of it measured the
        // normalized copy — the same track moved 99.40 to 106.97. It re-frames
        // too, so the final frame would not survive it either.
        string filterGraph = string.Join(
            ",",
            "aspectralstats=measure=centroid",
            "beatdetect",
            "keydetect",
            "ametadata=mode=print:file=-",
            "silencedetect=n=-50dB:d=0.5",
            "loudnorm=print_format=json"
        );

        string[] arguments =
        [
            "-nostdin",
            "-i",
            inputLease.Path,
            "-vn",
            "-sn",
            "-dn",
            "-af",
            filterGraph,
            "-f",
            "null",
            "-",
        ];

        // ffmpeg reading a stalled network mount never returns, and RunAsync
        // waits on the caller's token alone, so one unreadable file would hold
        // the analysis worker for good.
        using CancellationTokenSource analysisCts = CancellationTokenSource.CreateLinkedTokenSource(
            ct
        );
        analysisCts.CancelAfter(AnalysisTimeout);

        ProcessResult result;
        try
        {
            result = await processRunner.RunAsync(
                options.FfmpegPath,
                arguments,
                parser.ConsumeStdOut,
                parser.ConsumeStdErr,
                null,
                analysisCts.Token
            );
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(
                "audio analysis timed out after {Seconds}s for {Path}",
                AnalysisTimeout.TotalSeconds,
                filePath
            );
            return null;
        }

        if (!result.IsSuccess)
        {
            logger.LogWarning(
                "audio analysis failed for {Path} (exit {Exit}): {Stderr}",
                filePath,
                result.ExitCode,
                result.StdErr
            );
            return null;
        }

        AudioAnalysisResult analysis = parser.Build();

        // No verdict frame means no confidence and no phase — half the automix
        // inputs. Silently degrading there would look like a library of
        // untrustworthy tracks rather than something wrong with the pass. The
        // message reports what was observed; a stale binary is the likeliest
        // cause but not the only one, so it is not asserted.
        if (!analysis.BeatGridFromMetadata && analysis.Bpm is not null)
        {
            logger.LogWarning(
                "audio analysis found no beatdetect metadata for {Path} and fell back to the legacy stderr tempo; the ffmpeg build may predate v1.0.40",
                filePath
            );
        }

        // Every detector coming back empty means the pass produced nothing
        // usable, whatever the exit code said.
        bool measuredSomething =
            analysis.Bpm is not null
            || analysis.KeyName is not null
            || analysis.IntegratedLufs is not null;

        if (!measuredSomething)
        {
            logger.LogWarning("audio analysis produced no measurements for {Path}", filePath);
            return null;
        }

        return analysis;
    }
}
