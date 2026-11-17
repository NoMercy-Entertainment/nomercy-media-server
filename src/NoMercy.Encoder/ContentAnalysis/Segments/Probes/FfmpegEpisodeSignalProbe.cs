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
using System.Text;
using Microsoft.Extensions.Logging;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Encoder.Subtitles;
using NoMercy.NmSystem.Monitoring;
using NoMercy.Resources;
using NoMercy.Storage;

namespace NoMercy.Encoder.ContentAnalysis.Segments.Probes;

/// <summary>
/// <see cref="IEpisodeSignalProbe"/> on the server's own FFmpeg build. Binary
/// outputs (the raw fingerprint, thumbnails) and the OCR dump go to a private
/// temp folder through the local <see cref="IStorage"/>; filter reports are read
/// from stderr line by line. Each pass waits for playback to go idle first, like
/// the chromaprint fingerprinter it replaces.
/// </summary>
public sealed class FfmpegEpisodeSignalProbe(
    EncoderOptions options,
    IProcessRunner processRunner,
    IStorage storage,
    ITesseractModelManager tesseract,
    MediaActivityMonitor activityMonitor,
    ILogger<FfmpegEpisodeSignalProbe> logger
) : IEpisodeSignalProbe
{
    private const string OcrLanguage = "eng";

    private static readonly TimeSpan MaxIdleWait = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LongPassTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ShortPassTimeout = TimeSpan.FromMinutes(3);

    private static string Threads =>
        EncodeThreadBudget.AuxiliaryPass.ToString(CultureInfo.InvariantCulture);

    public async Task<AudioSignals?> ReadAudioAsync(SegmentMediaSource source, CancellationToken ct)
    {
        string folder = CreateTempFolder();
        string fingerprintPath = storage.CombinePath(folder, "fp.raw");
        try
        {
            await using LocalPathLease input = source.Storage.AcquireLocalPath(source.AudioPath);
            List<string> stderr = [];
            string[] args =
            [
                "-hide_banner",
                "-nostats",
                "-nostdin",
                "-threads",
                Threads,
                "-i",
                input.Path,
                "-filter_complex",
                $"[0:a:{source.AudioStreamIndex}]asplit[a1][a2];[a1]silencedetect=n=-50dB:d=0.3[ao]",
                "-map",
                "[ao]",
                "-f",
                "null",
                "-",
                "-map",
                "[a2]",
                "-f",
                "chromaprint",
                "-fp_format",
                "raw",
                "-y",
                fingerprintPath,
            ];

            if (!await RunAsync(args, stderr, LongPassTimeout, source.AudioPath, ct))
                return null;

            double? duration = FfmpegSignalParser.InputDuration(stderr);
            if (duration is null or <= 0 || !storage.Exists(fingerprintPath))
            {
                logger.LogWarning("Segment analysis: no duration or fingerprint for {Path}", source.AudioPath);
                return null;
            }

            byte[] raw = await storage.ReadAsync(fingerprintPath, ct);
            return new(
                duration.Value,
                ChromaprintRaw.Parse(raw),
                FfmpegSignalParser.Silences(stderr, duration.Value)
            );
        }
        finally
        {
            DeleteTempFolder(folder);
        }
    }

    public async Task<BlackRuns?> ReadBlackAsync(
        SegmentMediaSource source,
        IReadOnlyList<TimeRange> windows,
        CancellationToken ct
    )
    {
        await using LocalPathLease input = source.Storage.AcquireLocalPath(source.VideoPath);
        List<TimeRange> strict = [];
        List<TimeRange> loose = [];
        foreach (TimeRange window in windows)
        {
            List<string> stderr = [];
            string[] args =
            [
                .. WindowArgs(input.Path, window.Start, window.Length),
                "-vf",
                "scale=320:-2,blackdetect@strict=d=0.1:pix_th=0.10:pic_th=0.98,"
                    + "blackdetect@loose=d=1.0:pix_th=0.10:pic_th=0.80",
                "-f",
                "null",
                "-",
            ];
            if (!await RunAsync(args, stderr, ShortPassTimeout, source.VideoPath, ct))
                return null;

            strict.AddRange(FfmpegSignalParser.Black(stderr, window.Start, "strict"));
            loose.AddRange(FfmpegSignalParser.Black(stderr, window.Start, "loose"));
        }

        return new([.. strict.OrderBy(r => r.Start)], [.. loose.OrderBy(r => r.Start)]);
    }

    public async Task<OcrScan?> ReadTextAsync(
        SegmentMediaSource source,
        double start,
        CancellationToken ct
    )
    {
        string modelDirectory;
        try
        {
            string model = await tesseract.EnsureLanguageModelAsync(OcrLanguage, ct);
            modelDirectory = Path.GetDirectoryName(model) ?? string.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // OCR is optional: without it textured-background credits are missed,
            // nothing is skipped early.
            logger.LogInformation(ex, "Segment analysis: no OCR model ({Language}), OCR skipped", OcrLanguage);
            return null;
        }

        string folder = CreateTempFolder();
        // A bare name in the working directory: a drive colon inside a
        // filtergraph value cannot be escaped (see SubtitleOcrEngine).
        const string dumpName = "ocr.txt";
        string dumpPath = storage.CombinePath(folder, dumpName);
        try
        {
            await activityMonitor.WaitForIdleAsync(MaxIdleWait, ct);
            await using LocalPathLease input = source.Storage.AcquireLocalPath(source.VideoPath);
            string[] args =
            [
                "-hide_banner",
                "-nostats",
                "-nostdin",
                "-threads",
                Threads,
                "-ss",
                Seconds(start),
                "-i",
                input.Path,
                "-map",
                "0:v:0",
                "-an",
                "-sn",
                "-vf",
                $"fps=1,scale=960:-2,format=gray,ocr=language={OcrLanguage},"
                    + $"metadata=print:key=lavfi.ocr.text:file={dumpName}",
                "-f",
                "null",
                "-",
            ];

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(LongPassTimeout);
            ProcessResult result;
            try
            {
                result = await processRunner.RunAsync(
                    options.FfmpegPath,
                    args,
                    new Dictionary<string, string>
                    {
                        ["TESSDATA_PREFIX"] = modelDirectory,
                        ["OMP_THREAD_LIMIT"] = Threads,
                        ["OMP_NUM_THREADS"] = Threads,
                    },
                    workingDirectory: folder,
                    cancellationToken: timeout.Token
                );
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("Segment analysis: OCR timed out for {Path}", source.VideoPath);
                return null;
            }

            if (!result.IsSuccess || !storage.Exists(dumpPath))
            {
                logger.LogWarning(
                    "Segment analysis: OCR failed for {Path} (exit {Exit})",
                    source.VideoPath,
                    result.ExitCode
                );
                return null;
            }

            string dump = Encoding.UTF8.GetString(await storage.ReadAsync(dumpPath, ct));
            return new(start, FfmpegSignalParser.OcrWords(dump, start));
        }
        finally
        {
            DeleteTempFolder(folder);
        }
    }

    public async Task<EdgeProbe?> ProbeEdgeAsync(
        SegmentMediaSource source,
        double at,
        CancellationToken ct
    )
    {
        double start = Math.Max(0.0, at - SegmentRules.EdgeProbeSeconds);
        await using LocalPathLease input = source.Storage.AcquireLocalPath(source.VideoPath);
        List<string> stderr = [];
        string[] args =
        [
            .. WindowArgs(input.Path, start, 2 * SegmentRules.EdgeProbeSeconds),
            "-vf",
            "scale=320:-2,scdet=t=10,blackdetect=d=0.05:pix_th=0.10:pic_th=0.98",
            "-f",
            "null",
            "-",
        ];
        if (!await RunAsync(args, stderr, ShortPassTimeout, source.VideoPath, ct))
            return null;

        return new(
            FfmpegSignalParser.SceneCuts(stderr, start),
            FfmpegSignalParser.Black(stderr, start)
        );
    }

    public async Task<ThumbnailStrip?> ReadThumbnailsAsync(
        SegmentMediaSource source,
        TimeRange span,
        CancellationToken ct
    )
    {
        string folder = CreateTempFolder();
        string framesPath = storage.CombinePath(folder, "thumbs.gray");
        try
        {
            await using LocalPathLease input = source.Storage.AcquireLocalPath(source.VideoPath);
            List<string> stderr = [];
            string[] args =
            [
                .. WindowArgs(input.Path, span.Start, span.Length),
                "-vf",
                $"fps={SegmentRules.ThumbnailFps},scale={SegmentRules.ThumbnailWidth}:{SegmentRules.ThumbnailHeight},format=gray",
                "-f",
                "rawvideo",
                "-y",
                framesPath,
            ];
            if (!await RunAsync(args, stderr, ShortPassTimeout, source.VideoPath, ct) || !storage.Exists(framesPath))
                return null;

            byte[] pixels = await storage.ReadAsync(framesPath, ct);
            int frames = pixels.Length / SegmentRules.ThumbnailPixels;
            double[] times = new double[frames];
            for (int i = 0; i < frames; i++)
                times[i] = span.Start + (double)i / SegmentRules.ThumbnailFps;

            return new(times, pixels[..(frames * SegmentRules.ThumbnailPixels)]);
        }
        finally
        {
            DeleteTempFolder(folder);
        }
    }

    private static string[] WindowArgs(string input, double start, double length) =>
        [
            "-hide_banner",
            "-nostats",
            "-nostdin",
            "-threads",
            Threads,
            "-ss",
            Seconds(start),
            "-t",
            Seconds(length),
            "-i",
            input,
            "-map",
            "0:v:0",
            "-an",
            "-sn",
        ];

    private static string Seconds(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Runs ffmpeg once, after playback goes idle; false (logged) when it failed.</summary>
    private async Task<bool> RunAsync(
        string[] args,
        List<string> stderr,
        TimeSpan timeout,
        string path,
        CancellationToken ct
    )
    {
        await activityMonitor.WaitForIdleAsync(MaxIdleWait, ct);
        try
        {
            ProcessResult result = await processRunner.RunAsync(
                options.FfmpegPath,
                args,
                timeout,
                onStdOut: null,
                onStdErr: line =>
                {
                    lock (stderr)
                        stderr.Add(line);
                },
                workingDirectory: null,
                cancellationToken: ct
            );
            if (result.IsSuccess)
                return true;

            logger.LogWarning(
                "Segment analysis: ffmpeg exited {Exit} for {Path}: {Stderr}",
                result.ExitCode,
                path,
                result.StdErr
            );
            return false;
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Segment analysis: ffmpeg timed out for {Path}", path);
            return false;
        }
    }

    private string CreateTempFolder()
    {
        string folder = storage.CombinePath(
            Path.GetTempPath().Replace('\\', '/').TrimEnd('/'),
            "nm-segments",
            Guid.NewGuid().ToString("N")
        );
        storage.CreateDirectory(folder);
        return folder;
    }

    private void DeleteTempFolder(string folder)
    {
        try
        {
            if (storage.Exists(folder))
                storage.DeleteDirectory(folder, recursive: true);
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Segment analysis: could not delete {Folder}", folder);
        }
    }
}
