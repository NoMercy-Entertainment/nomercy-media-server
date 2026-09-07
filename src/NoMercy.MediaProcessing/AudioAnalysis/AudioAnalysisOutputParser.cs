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
/// The detectors answer on three different routes. <c>keydetect</c> and
/// <c>aspectralstats</c> set real frame metadata that <c>ametadata</c> prints to
/// stdout. <c>silencedetect</c>, <c>loudnorm</c> and the input header write to
/// the log on stderr. <c>beatdetect</c> writes a bare stderr line because it
/// sets no metadata at all.
/// </para>
/// <para>
/// Everything except beatdetect logs at info level, so the pass must not be run
/// with a quieter loglevel or the silence, loudness and duration all vanish
/// while the tempo still appears — a failure that looks like partial detection
/// rather than a misconfigured command.
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
    /// The disagreement at which a tempo is worth nothing. Measured across a real
    /// library: dense produced material moves under 2% between sample rates,
    /// while material the detector cannot hold moves by 19% to 63%.
    /// </summary>
    private const double MaxTrustedSpread = 0.10;

    [GeneratedRegex(@"^lavfi\.(?<key>[a-z0-9_.]+)=(?<value>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex MetadataLineRegex();

    // beatdetect names its key like frame metadata but only logs it, so it has
    // to be scraped. Deleted when nomercy-ffmpeg#57 A1 lands and the key arrives
    // through ametadata with everything else.
    //
    // Only the tagged av_log form is read. The filter also writes the same value
    // through a bare fprintf that carries no instance tag, so two instances in
    // one graph produce four lines of which only two can be told apart.
    [GeneratedRegex(
        @"\[Parsed_beatdetect_(?<instance>[0-9]+) @[^]]*\]\s*lavfi\.beatdetect\.bpm=(?<bpm>[0-9]+(?:\.[0-9]+)?)"
    )]
    private static partial Regex BeatdetectStderrRegex();

    [GeneratedRegex(@"silence_start:\s*(?<value>-?[0-9]+(?:\.[0-9]+)?)")]
    private static partial Regex SilenceStartRegex();

    [GeneratedRegex(@"silence_end:\s*(?<value>-?[0-9]+(?:\.[0-9]+)?)")]
    private static partial Regex SilenceEndRegex();

    [GeneratedRegex(
        @"Duration:\s*(?<h>\d+):(?<m>\d{2}):(?<s>\d{2}(?:\.\d+)?)",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex DurationRegex();

    private readonly StringBuilder _loudnormJson = new();
    private readonly List<double> _centroids = [];
    private readonly List<SilenceRegion> _silences = [];

    private readonly Dictionary<int, double> _bpmByInstance = [];

    private bool _collectingLoudnorm;
    private double? _bpm;
    private string? _keyName;
    private double? _keyConfidence;
    private double? _durationSeconds;

    public void ConsumeStdOut(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        Match match = MetadataLineRegex().Match(line.Trim());
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
        JObject? loudness = ParseLoudnormJson();
        double? centroid = _centroids.Count > 0 ? _centroids.Average() : null;
        double? beatInterval = _bpm is > 0 ? 60000.0 / _bpm : null;

        return new AudioAnalysisResult
        {
            Bpm = _bpm,

            BpmConfidence = ResolveBpmConfidence(),

            // beatdetect emits no downbeat, so phase stays unset rather than
            // invented; nomercy-ffmpeg#57 A2 fills it.
            BeatOffsetMs = null,
            BeatIntervalMs = beatInterval,

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
        _bpmByInstance[instance] = parsed.Value;

        // The first instance analyses the track as delivered; anything after it
        // is a perturbed copy used only to judge stability.
        if (instance == _bpmByInstance.Keys.Min())
        {
            _bpm = parsed;
        }
    }

    /// <summary>
    /// How stable the tempo is under a change that must not alter it — the graph
    /// runs a second detector over the same audio at a different sample rate.
    /// <para>
    /// This measures stability, NOT correctness. While the filter's tempo priors
    /// remain (nomercy-ffmpeg#57 B5), a confidently wrong answer stays confidently
    /// wrong under resampling and scores high. What it reliably catches is the
    /// opposite case: material the detector cannot lock onto at all, which moves
    /// by tens of percent between rates.
    /// </para>
    /// <para>Null when the graph ran only one detector, so a caller can tell
    /// "not measured" from "measured as unreliable".</para>
    /// </summary>
    private double? ResolveBpmConfidence()
    {
        if (_bpmByInstance.Count < 2 || _bpm is not > 0)
        {
            return null;
        }

        double baseline = _bpm.Value;
        double worstSpread = _bpmByInstance
            .Values.Select(value => Math.Abs(value - baseline) / baseline)
            .Max();

        return Math.Clamp(1.0 - worstSpread / MaxTrustedSpread, 0.0, 1.0);
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
        catch (Exception)
        {
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
