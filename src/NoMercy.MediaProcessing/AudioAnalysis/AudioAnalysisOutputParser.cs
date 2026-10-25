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
using Newtonsoft.Json.Linq;

namespace NoMercy.MediaProcessing.AudioAnalysis;

/// <summary>
/// Reads one analysis pass line by line and assembles the result.
/// <para>
/// The detectors answer on two routes. <c>beatdetect</c>, <c>keydetect</c> and
/// <c>aspectralstats</c> set frame metadata that <c>ametadata</c> prints to
/// stdout as a <c>frame:N pts:... pts_time:...</c> header followed by
/// <c>key=value</c> lines. <c>silencedetect</c>, <c>loudnorm</c> and the input
/// header write to the log on stderr.
/// </para>
/// <para>
/// Only the beatdetect frame tagged <c>final=1</c> holds the tempo verdict and
/// the beat grid; every earlier frame carries a running estimate that sits at
/// half time for most of a pass. Builds older than nomercy-ffmpeg v1.0.40 print
/// no beatdetect metadata at all, so the tagged stderr tempo line stays as a
/// fallback and <see cref="AudioAnalysisResult.BeatGridFromMetadata" /> says
/// which route answered. v1.0.40 still logs that line too, so it is not a
/// version marker: it is consulted only when no verdict frame arrived, whatever
/// the build.
/// </para>
/// <para>
/// The stderr half logs at info level while the stdout half ignores loglevel
/// entirely, so the pass must not be run with a quieter loglevel or the silence,
/// loudness and duration all vanish while the tempo and key still appear — a
/// failure that looks like partial detection rather than a misconfigured
/// command.
/// </para>
/// <para>
/// Consuming line by line rather than buffering matters: metadata is printed on
/// every frame, so a long track prints megabytes.
/// </para>
/// </summary>
public sealed partial class AudioAnalysisOutputParser
{
    private const double IntroSilenceToleranceSeconds = 0.05;
    private const double OutroSilenceToleranceSeconds = 0.25;

    /// <summary>
    /// How far a verdict's beat interval may sit from the one its tempo implies
    /// before the pair is treated as coming from two different frames.
    /// </summary>
    private const double VerdictTempoTolerance = 0.03;

    [GeneratedRegex(@"^lavfi\.(?<key>[a-z0-9_.]+)=(?<value>.*)$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MetadataLineRegex();

    /// <summary>
    /// The header <c>ametadata</c> writes before each frame's keys. It is the
    /// only marker of where one frame's metadata ends and the next begins.
    /// </summary>
    [GeneratedRegex(@"^frame:\s*[0-9]+\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FrameHeaderRegex();

    // The fallback for builds older than nomercy-ffmpeg v1.0.40, which publish
    // no beatdetect metadata. Only the tagged av_log form is read: the filter
    // also writes the same value through a bare fprintf carrying no instance
    // tag, which cannot be attributed to a detector.
    [GeneratedRegex(
        @"\[Parsed_beatdetect_(?<instance>[0-9]+) @[^]]*\]\s*lavfi\.beatdetect\.bpm=(?<bpm>[0-9]+(?:\.[0-9]+)?)",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex BeatdetectStderrRegex();

    [GeneratedRegex(@"silence_start:\s*(?<value>-?[0-9]+(?:\.[0-9]+)?)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SilenceStartRegex();

    [GeneratedRegex(@"silence_end:\s*(?<value>-?[0-9]+(?:\.[0-9]+)?)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SilenceEndRegex();

    [GeneratedRegex(
        @"Duration:\s*(?<h>\d+):(?<m>\d{2}):(?<s>\d{2}(?:\.\d+)?)",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex DurationRegex();

    private readonly StringBuilder _loudnormJson = new();
    private readonly List<double> _centroids = [];
    private readonly List<SilenceRegion> _silences = [];

    private readonly Dictionary<string, string> _beatdetectFrame = [];

    private bool _collectingLoudnorm;
    private bool _beatGridFromMetadata;
    private double? _bpm;
    private double? _bpmConfidence;
    private double? _beatIntervalMs;
    private int? _beatOffsetMs;
    private double? _legacyBpm;
    private int? _legacyBpmInstance;
    private string? _keyName;
    private double? _keyConfidence;
    private double? _durationSeconds;

    public void ConsumeStdOut(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        string trimmed = line.Trim();

        if (FrameHeaderRegex().IsMatch(trimmed))
        {
            FlushBeatdetectFrame();
            return;
        }

        Match match = MetadataLineRegex().Match(trimmed);
        if (!match.Success)
        {
            return;
        }

        string key = match.Groups["key"].Value;
        string value = match.Groups["value"].Value.Trim();

        switch (key)
        {
            case "keydetect.key":
                _keyName = value;
                break;
            case "keydetect.key_confidence":
                _keyConfidence = ParseDouble(value);
                break;
            default:
                CollectBeatdetectKey(key, value);
                CollectCentroid(key, value);
                break;
        }
    }

    public void ConsumeStdErr(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        CollectBpm(line);
        CollectDuration(line);
        CollectSilence(line);
        CollectLoudnormJson(line);
    }

    public AudioAnalysisResult Build()
    {
        // The final frame is the last thing beatdetect publishes, so no frame
        // header follows it to trigger the flush that every other frame gets.
        FlushBeatdetectFrame();

        JObject? loudness = ParseLoudnormJson();
        double? centroid = _centroids.Count > 0 ? _centroids.Average() : null;

        double? bpm = _beatGridFromMetadata ? _bpm : _legacyBpm;

        return new AudioAnalysisResult
        {
            Bpm = bpm,

            BpmConfidence = _beatGridFromMetadata ? _bpmConfidence : null,

            // An older build reports a tempo and nothing else, so phase stays
            // unset rather than invented and the interval is derived from the
            // tempo alone.
            BeatOffsetMs = _beatGridFromMetadata ? _beatOffsetMs : null,
            BeatIntervalMs = ResolveBeatIntervalMs(bpm),
            BeatGridFromMetadata = _beatGridFromMetadata,

            KeyName = string.IsNullOrWhiteSpace(_keyName) ? null : _keyName,
            KeyConfidence = _keyConfidence,

            IntegratedLufs = ReadLoudnessValue(loudness, "input_i"),
            TruePeakDb = ReadLoudnessValue(loudness, "input_tp"),
            LoudnessRange = ReadLoudnessValue(loudness, "input_lra"),
            SpectralCentroid = centroid,

            IntroEndMs = ResolveIntroEndMs(),
            OutroStartMs = ResolveOutroStartMs(),
        };
    }

    private void CollectBeatdetectKey(string key, string value)
    {
        const string prefix = "beatdetect.";

        if (!key.StartsWith(prefix, StringComparison.Ordinal))
        {
            return;
        }

        _beatdetectFrame[key[prefix.Length..]] = value;
    }

    /// <summary>
    /// Takes the verdict from the frame whose keys just ended, but only when
    /// beatdetect tagged that frame <c>final=1</c> and the frame is whole.
    /// Every earlier frame carries a running estimate that spends most of a pass
    /// an octave low, so taking the newest value instead of the final one halves
    /// the tempo.
    /// <para>
    /// Whole means all four values, agreeing with each other. A frame is a text
    /// block that another writer on the same stream can cut in half: two
    /// <c>ametadata</c> instances printing to <c>file=-</c> were measured
    /// splicing each other mid-line, and a cut after <c>bpm=</c> orphans the
    /// <c>final=1</c> onto the following frame — whose running estimate would
    /// then be stored as a measured grid. The graph runs one writer now; this
    /// rejects the damage anyway, because a verdict that cannot be checked is
    /// worth less than the legacy tempo it falls back to.
    /// </para>
    /// <para>The last whole final frame wins, so a build that lets the verdict
    /// through more than one print cannot turn it into two.</para>
    /// </summary>
    private void FlushBeatdetectFrame()
    {
        if (_beatdetectFrame.Count == 0)
        {
            return;
        }

        bool isFinal = _beatdetectFrame.GetValueOrDefault("final") is "1";
        double? bpm = ParseDouble(_beatdetectFrame.GetValueOrDefault("bpm"));
        double? confidence = ParseDouble(_beatdetectFrame.GetValueOrDefault("confidence"));
        double? interval = ParseDouble(_beatdetectFrame.GetValueOrDefault("beat_interval_ms"));
        double? offset = ParseDouble(_beatdetectFrame.GetValueOrDefault("beat_offset_ms"));

        _beatdetectFrame.Clear();

        // The filter reports 0.00 when it locked onto nothing. That is an
        // absence, not a tempo of zero and not a grid worth recording.
        if (
            !isFinal
            || bpm is not > 0
            || confidence is null
            || interval is not > 0
            || offset is null
        )
        {
            return;
        }

        // The two survivors of a splice come from different frames, so the
        // interval stops implying the tempo — an octave apart, not a rounding
        // apart. The tolerance is far wider than the two decimals the filter
        // prints and far narrower than anything a mixed pair produces.
        if (Math.Abs(60000.0 / interval.Value - bpm.Value) > VerdictTempoTolerance * bpm.Value)
        {
            return;
        }

        _bpm = bpm;
        _bpmConfidence = confidence;
        _beatIntervalMs = interval;
        _beatOffsetMs = ToWholeMilliseconds(offset);
        _beatGridFromMetadata = true;
    }

    /// <summary>
    /// The measured interval when there is one, otherwise the one the tempo
    /// implies. They differ: a measured grid is the average spacing of the
    /// detected beats, not 60000 divided by a rounded tempo.
    /// </summary>
    private double? ResolveBeatIntervalMs(double? bpm)
    {
        if (_beatGridFromMetadata && _beatIntervalMs is > 0)
        {
            return _beatIntervalMs;
        }

        return bpm is > 0 ? 60000.0 / bpm : null;
    }

    /// <summary>
    /// The tempo an older ffmpeg logged instead of publishing it as metadata.
    /// Only the lowest instance counts: it is the detector that saw the audio as
    /// delivered. Within that instance the first line wins, because the filter
    /// logs its tempo once and a repeat is an echo of the same measurement, not
    /// a second one.
    /// </summary>
    private void CollectBpm(string line)
    {
        Match match = BeatdetectStderrRegex().Match(line);
        if (!match.Success)
        {
            return;
        }

        double? parsed = ParseDouble(match.Groups["bpm"].Value);

        // The filter prints 0.00 when it found nothing. That is an absence, not
        // a tempo of zero.
        if (parsed is not > 0)
        {
            return;
        }

        int instance = int.Parse(match.Groups["instance"].Value, CultureInfo.InvariantCulture);

        if (_legacyBpmInstance is not null && instance >= _legacyBpmInstance)
        {
            return;
        }

        _legacyBpmInstance = instance;
        _legacyBpm = parsed;
    }

    private void CollectDuration(string line)
    {
        if (_durationSeconds is not null)
        {
            return;
        }

        Match match = DurationRegex().Match(line);
        if (!match.Success)
        {
            return;
        }

        double? hours = ParseDouble(match.Groups["h"].Value);
        double? minutes = ParseDouble(match.Groups["m"].Value);
        double? seconds = ParseDouble(match.Groups["s"].Value);

        if (hours is null || minutes is null || seconds is null)
        {
            return;
        }

        _durationSeconds = hours.Value * 3600.0 + minutes.Value * 60.0 + seconds.Value;
    }

    private void CollectSilence(string line)
    {
        Match start = SilenceStartRegex().Match(line);
        if (start.Success)
        {
            double? value = ParseDouble(start.Groups["value"].Value);
            if (value is not null)
            {
                _silences.Add(new SilenceRegion { Start = value.Value });
            }

            return;
        }

        Match end = SilenceEndRegex().Match(line);
        if (!end.Success || _silences.Count == 0)
        {
            return;
        }

        double? endValue = ParseDouble(end.Groups["value"].Value);
        if (endValue is not null)
        {
            _silences[^1].End = endValue;
        }
    }

    private void CollectCentroid(string key, string value)
    {
        if (!key.StartsWith("aspectralstats.", StringComparison.Ordinal))
        {
            return;
        }

        if (!key.EndsWith(".centroid", StringComparison.Ordinal))
        {
            return;
        }

        double? parsed = ParseDouble(value);
        if (parsed is > 0)
        {
            _centroids.Add(parsed.Value);
        }
    }

    private void CollectLoudnormJson(string line)
    {
        string trimmed = line.Trim();

        if (!_collectingLoudnorm && trimmed.StartsWith('{'))
        {
            _collectingLoudnorm = true;
            _loudnormJson.Clear();
        }

        if (!_collectingLoudnorm)
        {
            return;
        }

        _loudnormJson.AppendLine(trimmed);

        if (trimmed.StartsWith('}'))
        {
            _collectingLoudnorm = false;
        }
    }

    private JObject? ParseLoudnormJson()
    {
        if (_loudnormJson.Length == 0)
        {
            return null;
        }

        try
        {
            return JObject.Parse(_loudnormJson.ToString());
        }
        catch (Exception e)
        {
            // Loudness is one of three detectors, so a broken block is not a
            // failed analysis — but it must not vanish without a trace either.
            Console.WriteLine($"audio analysis: loudnorm block did not parse: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Leading silence counts as an intro only when the file actually starts
    /// silent. A quiet passage in the middle is not a mix-in point.
    /// </summary>
    private int? ResolveIntroEndMs()
    {
        SilenceRegion? first = _silences.FirstOrDefault();

        if (first?.End is null || first.Start > IntroSilenceToleranceSeconds)
        {
            return null;
        }

        return ToMilliseconds(first.End.Value);
    }

    /// <summary>
    /// Trailing silence is the last region, and it only counts when it runs to
    /// the end of the file. silencedetect flushes an end at EOF, so a region
    /// that ends well before the duration is a gap rather than an outro.
    /// </summary>
    private int? ResolveOutroStartMs()
    {
        SilenceRegion? last = _silences.LastOrDefault();

        if (last is null)
        {
            return null;
        }

        if (last.End is null)
        {
            return ToMilliseconds(last.Start);
        }

        if (_durationSeconds is null)
        {
            return null;
        }

        bool runsToEnd = _durationSeconds.Value - last.End.Value <= OutroSilenceToleranceSeconds;

        return runsToEnd ? ToMilliseconds(last.Start) : null;
    }

    private static int ToMilliseconds(double seconds)
    {
        return (int)Math.Round(seconds * 1000.0, MidpointRounding.AwayFromZero);
    }

    private static int? ToWholeMilliseconds(double? milliseconds)
    {
        if (milliseconds is null)
        {
            return null;
        }

        return (int)Math.Round(milliseconds.Value, MidpointRounding.AwayFromZero);
    }

    private static double? ReadLoudnessValue(JObject? loudness, string property)
    {
        string? raw = loudness?.Value<string>(property);
        return ParseDouble(raw);
    }

    private static double? ParseDouble(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        bool parsed = double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double result
        );

        if (!parsed || double.IsNaN(result) || double.IsInfinity(result))
        {
            return null;
        }

        return result;
    }

    private sealed class SilenceRegion
    {
        public required double Start { get; init; }
        public double? End { get; set; }
    }
}
