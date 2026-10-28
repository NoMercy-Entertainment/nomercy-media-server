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
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Encoder.Progress;
using NoMercy.Resources;
using NoMercy.Storage;

namespace NoMercy.Encoder.Subtitles;

/// <summary>
/// Converts bitmap subtitle tracks (PGS / VobSub / DVB) into text subtitle files
/// by running FFmpeg's <c>ocr</c> filter with the libtesseract backend. Reads
/// the OCR output via the <c>metadata=print</c> filter, parses the
/// <c>lavfi.ocr.text</c> cue stream, and writes WebVTT or SRT.
/// </summary>
public partial class SubtitleOcrEngine(
    EncoderOptions options,
    IProcessRunner processRunner,
    ITesseractModelManager modelManager,
    IStorage storage,
    ILogger<SubtitleOcrEngine> logger,
    IAnalysisProgressObserver? progress = null
) : ISubtitleOcrEngine
{
    public async Task<SubtitleTrack> OcrAsync(
        string inputPath,
        int streamIndex,
        string language,
        SubtitleCodecType outputFormat,
        CancellationToken ct,
        IStorage? sourceStorage = null,
        OcrSidecarTarget? sidecar = null
    )
    {
        // Input and sidecar each live wherever their caller says, which is rarely
        // this engine's injected storage: that one is local, so a key relative to
        // an NFS/S3 driver resolves under the wrong root — the input is looked for
        // on the local disk, and the sidecar is written under the server's working
        // directory. The temp metadata file is the only genuinely local artefact.
        IStorage inputStorage = sourceStorage ?? storage;
        IStorage sidecarStorage = sidecar?.Storage ?? storage;

        // Pull the language model before invoking FFmpeg so the OCR filter
        // actually has training data when it runs.
        string modelPath = await modelManager.EnsureLanguageModelAsync(language, ct);
        string modelDirectory =
            Path.GetDirectoryName(modelPath)
            ?? throw new InvalidOperationException("Tesseract model has no parent directory");

        // OCR dumps its metadata to a file so we never have to parse FFmpeg's
        // interleaved stdout. Write it to a unique temp file per run so
        // concurrent OCR jobs can't collide.
        string tempDirectory = Path.Combine(Path.GetTempPath(), "nm-ocr");
        storage.CreateDirectory(tempDirectory);
        // The metadata filter's file= value is a bare name written into the
        // process working directory, never an absolute path: a Windows drive
        // colon inside a filtergraph value is unescapable and aborts the whole
        // ocr filter parse (the recurring "bitmap subs never became vtt" bug).
        string ocrFileName = $"ocr-{Guid.NewGuid():N}.txt";
        string ocrOutput = Path.Combine(tempDirectory, ocrFileName);

        string jobId = Guid.NewGuid().ToString("N");
        IAnalysisProgressObserver observer = progress ?? NullAnalysisProgressObserver.Instance;
        observer.Report(jobId, "ocr", 0, $"starting ocr ({language})");

        string extension = outputFormat == SubtitleCodecType.Srt ? ".srt" : ".vtt";
        string outputPath = ResolveOutputPath(inputPath, sidecar, language, extension);

        try
        {
            // Lease the input so future remote drivers can stage it locally and
            // clean up on dispose. The input is a plain -i argument (not part of
            // the filtergraph), so its absolute Windows path is fine.
            await using LocalPathLease inputLease = inputStorage.AcquireLocalPath(inputPath);

            string? outputParentDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputParentDirectory))
                sidecarStorage.CreateDirectory(outputParentDirectory);

            // Tesseract's data directory rides in on TESSDATA_PREFIX instead of the
            // filter's datapath= option: a datapath with a drive colon cannot be
            // escaped inside a filtergraph and aborts the parse. The metadata file
            // is a bare name resolved against the working directory for the same
            // reason.
            string[] args =
            [
                "-hide_banner",
                // Ahead of -i: after it, the cap reaches the output and leaves the
                // decoder to take the box.
                "-threads",
                EncodeThreadBudget.AuxiliaryPass.ToString(CultureInfo.InvariantCulture),
                "-i",
                inputLease.Path,
                "-filter_complex",
                $"[0:s:{streamIndex}]ocr=language={language},metadata=print:key=lavfi.ocr.text:file={ocrFileName}",
                "-an",
                "-filter_complex_threads",
                EncodeThreadBudget.AuxiliaryPass.ToString(CultureInfo.InvariantCulture),
                "-f",
                "null",
                "-",
            ];

            string auxThreads = EncodeThreadBudget.AuxiliaryPass.ToString(
                CultureInfo.InvariantCulture
            );
            ProcessResult result = await processRunner.RunAsync(
                options.FfmpegPath,
                args,
                new Dictionary<string, string>
                {
                    ["TESSDATA_PREFIX"] = modelDirectory,
                    // libtesseract's own OpenMP pool ignores ffmpeg's -threads flag.
                    ["OMP_THREAD_LIMIT"] = auxThreads,
                    ["OMP_NUM_THREADS"] = auxThreads,
                },
                workingDirectory: tempDirectory,
                cancellationToken: ct
            );

            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(
                    $"OCR ffmpeg exited with code {result.ExitCode}: {TrimErr(result.StdErr)}"
                );
            }

            if (!storage.Exists(ocrOutput))
            {
                throw new InvalidOperationException(
                    "OCR produced no output file — check the subtitle stream index and language model."
                );
            }

            byte[] ocrBytes = await storage.ReadAsync(ocrOutput, ct);
            List<SubtitleCue> cues = ParseOcrOutput(Encoding.UTF8.GetString(ocrBytes));

            if (outputFormat == SubtitleCodecType.Srt)
                await WriteSrtAsync(sidecarStorage, outputPath, cues, ct);
            else
                await WriteWebVttAsync(sidecarStorage, outputPath, cues, ct);

            logger.LogInformation(
                "OCR produced {CueCount} cues for {Language} → {Path}",
                cues.Count,
                language,
                outputPath
            );

            observer.Report(jobId, "ocr", 100, "done");

            return new(outputPath, language, outputFormat, cues.Count);
        }
        finally
        {
            try
            {
                storage.Delete(ocrOutput);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Could not delete OCR temp file {Path}", ocrOutput);
            }
        }
    }

    /// <summary>
    /// Parses <c>metadata=print</c> output from FFmpeg's <c>ocr</c> filter.
    /// The filter emits one pts_time line per frame, optionally followed by
    /// <c>lavfi.ocr.text=</c> blocks. We stream the file line-by-line, tracking
    /// the current pts and the most recent text, and emit a cue every time the
    /// text changes — collapsing runs of identical text into one cue that spans
    /// the full duration the text was visible.
    /// </summary>
    internal static List<SubtitleCue> ParseOcrOutput(string content)
    {
        List<SubtitleCue> cues = [];

        string? currentText = null;
        double currentStart = 0;
        double currentLastSeen = 0;
        double latestPts = 0;

        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r', ' ', '\t');
            if (string.IsNullOrEmpty(line))
                continue;

            Match ptsMatch = PtsTimeRegex().Match(line);
            if (ptsMatch.Success)
            {
                // FFmpeg lavfi.ocr output occasionally emits non-numeric PTS
                // tokens for malformed PGS streams. TryParse + skip keeps the
                // OCR pipeline running across the bad line instead of taking
                // the whole subtitle pass down with FormatException.
                if (
                    double.TryParse(
                        ptsMatch.Groups["pts"].Value,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double parsedPts
                    )
                )
                    latestPts = parsedPts;
                continue;
            }

            if (!line.StartsWith("lavfi.ocr.text=", StringComparison.Ordinal))
                continue;

            string text = line["lavfi.ocr.text=".Length..].Trim();

            if (string.IsNullOrEmpty(text))
            {
                if (currentText is not null)
                {
                    cues.Add(new(currentStart, currentLastSeen, currentText));
                    currentText = null;
                }
                continue;
            }

            if (currentText is null)
            {
                currentText = text;
                currentStart = latestPts;
                currentLastSeen = latestPts;
            }
            else if (!string.Equals(currentText, text, StringComparison.Ordinal))
            {
                cues.Add(new(currentStart, currentLastSeen, currentText));
                currentText = text;
                currentStart = latestPts;
                currentLastSeen = latestPts;
            }
            else
            {
                // Same text as current cue — extend its visible window to this pts.
                currentLastSeen = latestPts;
            }
        }

        if (currentText is not null)
            cues.Add(new(currentStart, currentLastSeen, currentText));

        return cues;
    }

    /// <summary>
    /// Resolves where the OCR sidecar lands. With no <paramref name="outputDirectory"/>
    /// (the ad-hoc dashboard/spot-check callers) it keeps the historical
    /// next-to-input placement. With an <paramref name="outputDirectory"/> (the
    /// encode pipeline) it lands in the same <c>subtitles/</c> subfolder and
    /// <c>{lang}.{type}.{ext}</c> naming the post-encode library scan already
    /// uses to discover real text-subtitle sidecars (FileManager's
    /// SubtitleFileRegex) — so the scan picks the OCR output up automatically.
    /// The stream index rides along in the "type" segment so two same-language
    /// bitmap streams never collide.
    /// </summary>
    private static string ResolveOutputPath(
        string inputPath,
        OcrSidecarTarget? sidecar,
        string language,
        string extension
    )
    {
        if (sidecar is null)
        {
            return Path.ChangeExtension(
                Path.Combine(Path.GetDirectoryName(inputPath)!, $"{language}_ocr"),
                extension
            );
        }

        // subtitles/{filename}.{lang}.{type} — the extraction pass's template
        // (BuiltinPresets), which is what makes this the .mks/.sup's sibling and
        // therefore a track the library scan pairs and the player lists.
        // Forward slashes: this is a storage key, not a local path.
        return $"{sidecar.OutputDirectory.TrimEnd('/')}/subtitles/"
            + $"{sidecar.MediaTitle}.{language}.{sidecar.Variant}{extension}";
    }

    private static async Task WriteWebVttAsync(
        IStorage sidecarStorage,
        string path,
        IEnumerable<SubtitleCue> cues,
        CancellationToken ct
    )
    {
        StringBuilder sb = new();
        sb.AppendLine("WEBVTT");
        sb.AppendLine();

        int index = 1;
        foreach (SubtitleCue cue in cues)
        {
            sb.AppendLine(index.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine($"{FormatVttTime(cue.StartSeconds)} --> {FormatVttTime(cue.EndSeconds)}");
            sb.AppendLine(cue.Text);
            sb.AppendLine();
            index++;
        }

        await sidecarStorage.WriteAsync(path, Encoding.UTF8.GetBytes(sb.ToString()), ct);
    }

    private static async Task WriteSrtAsync(
        IStorage sidecarStorage,
        string path,
        IEnumerable<SubtitleCue> cues,
        CancellationToken ct
    )
    {
        StringBuilder sb = new();
        int index = 1;
        foreach (SubtitleCue cue in cues)
        {
            sb.AppendLine(index.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine($"{FormatSrtTime(cue.StartSeconds)} --> {FormatSrtTime(cue.EndSeconds)}");
            sb.AppendLine(cue.Text);
            sb.AppendLine();
            index++;
        }

        await sidecarStorage.WriteAsync(path, Encoding.UTF8.GetBytes(sb.ToString()), ct);
    }

    private static string FormatVttTime(double seconds)
    {
        TimeSpan ts = TimeSpan.FromSeconds(seconds);
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:00}:{1:00}:{2:00}.{3:000}",
            (int)ts.TotalHours,
            ts.Minutes,
            ts.Seconds,
            ts.Milliseconds
        );
    }

    private static string FormatSrtTime(double seconds)
    {
        TimeSpan ts = TimeSpan.FromSeconds(seconds);
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:00}:{1:00}:{2:00},{3:000}",
            (int)ts.TotalHours,
            ts.Minutes,
            ts.Seconds,
            ts.Milliseconds
        );
    }

    private static string TrimErr(string stdErr)
    {
        if (string.IsNullOrEmpty(stdErr))
            return "(no stderr)";
        string[] lines = stdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length <= 5 ? stdErr : string.Join('\n', lines[^5..]);
    }

    [GeneratedRegex(@"pts_time:(?<pts>\d+(?:\.\d+)?)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PtsTimeRegex();

    public record SubtitleCue(double StartSeconds, double EndSeconds, string Text);
}
