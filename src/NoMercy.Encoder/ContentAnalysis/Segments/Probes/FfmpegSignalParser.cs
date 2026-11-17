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

namespace NoMercy.Encoder.ContentAnalysis.Segments.Probes;

/// <summary>
/// Reads the FFmpeg filter reports the segment analysis runs on: silencedetect,
/// blackdetect (optionally a named instance), scdet and the ocr filter's
/// metadata dump. Times in a windowed run are relative to the window, so every
/// parser takes the window start as an offset.
/// </summary>
internal static partial class FfmpegSignalParser
{
    /// <summary>
    /// silencedetect runs. The start of a closed run is end − duration (the
    /// start line rounds differently); a run still open at the end of the input
    /// closes at <paramref name="duration"/>.
    /// </summary>
    public static List<TimeRange> Silences(IEnumerable<string> lines, double duration, double offset = 0)
    {
        List<TimeRange> silences = [];
        double? open = null;
        foreach (string line in lines)
        {
            Match end = SilenceEndRegex().Match(line);
            if (end.Success)
            {
                double stop = Number(end.Groups[1].Value);
                double length = Number(end.Groups[2].Value);
                silences.Add(new(offset + stop - length, offset + stop));
                open = null;
                continue;
            }

            Match start = SilenceStartRegex().Match(line);
            if (start.Success)
                open = offset + Number(start.Groups[1].Value);
        }

        if (open is not null)
            silences.Add(new(open.Value, duration));

        return [.. silences.OrderBy(s => s.Start).ThenBy(s => s.End)];
    }

    /// <summary>blackdetect runs, of the instance named <paramref name="instance"/> when given.</summary>
    public static List<TimeRange> Black(IEnumerable<string> lines, double offset = 0, string? instance = null)
    {
        List<TimeRange> runs = [];
        foreach (string line in lines)
        {
            Match match = BlackRegex().Match(line);
            if (!match.Success)
                continue;
            if (instance is not null && match.Groups["instance"].Value != instance)
                continue;
            runs.Add(new(offset + Number(match.Groups["start"].Value), offset + Number(match.Groups["end"].Value)));
        }

        return runs;
    }

    /// <summary>scdet scene cuts.</summary>
    public static List<double> SceneCuts(IEnumerable<string> lines, double offset = 0)
    {
        List<double> cuts = [];
        foreach (string line in lines)
        {
            Match match = SceneCutRegex().Match(line);
            if (match.Success)
                cuts.Add(offset + Number(match.Groups[1].Value));
        }

        return cuts;
    }

    /// <summary>The input's container duration from FFmpeg's "Duration: HH:MM:SS.ss" line.</summary>
    public static double? InputDuration(IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            Match match = DurationRegex().Match(line);
            if (!match.Success)
                continue;
            return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
                + int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                + Number(match.Groups[3].Value);
        }

        return null;
    }

    /// <summary>
    /// Words of 3+ letters per frame from the ocr filter's
    /// <c>metadata=print:key=lavfi.ocr.text:file=...</c> dump.
    /// </summary>
    public static List<OcrSample> OcrWords(string dump, double offset = 0)
    {
        List<OcrSample> samples = [];
        string[] chunks = FrameRegex().Split(dump);
        // Split keeps the captured pts_time: [before, t0, text0, t1, text1, ...].
        for (int i = 1; i + 1 < chunks.Length; i += 2)
        {
            string text = chunks[i + 1].Replace("lavfi.ocr.text=", string.Empty, StringComparison.Ordinal);
            int words = WordRegex().Matches(text).Count;
            samples.Add(new(Math.Round(offset + Number(chunks[i]), 2), words));
        }

        return samples;
    }

    private static double Number(string value) => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"silence_start: (-?[\d.]+)")]
    private static partial Regex SilenceStartRegex();

    [GeneratedRegex(@"silence_end: (-?[\d.]+) \| silence_duration: ([\d.]+)")]
    private static partial Regex SilenceEndRegex();

    [GeneratedRegex(@"\[blackdetect(?:@(?<instance>\w+))? @ [^\]]+\] black_start:(?<start>[\d.]+) black_end:(?<end>[\d.]+)")]
    private static partial Regex BlackRegex();

    [GeneratedRegex(@"lavfi\.scd\.score: [\d.]+, lavfi\.scd\.time: ([\d.]+)")]
    private static partial Regex SceneCutRegex();

    [GeneratedRegex(@"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)")]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"frame:\d+\s+pts:\d+\s+pts_time:([\d.]+)")]
    private static partial Regex FrameRegex();

    [GeneratedRegex("[A-Za-z]{3,}")]
    private static partial Regex WordRegex();
}
