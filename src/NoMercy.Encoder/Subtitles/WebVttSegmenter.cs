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
using NoMercy.Storage;

namespace NoMercy.Encoder.Subtitles;

/// <summary>
/// Slices a WebVTT file into per-HLS-segment .vtt files.
/// Each segment covers [N*duration, (N+1)*duration). Cues that straddle a
/// segment boundary are duplicated into both segments (per the HLS subtitle
/// spec, RFC 8216 §3.5).
/// </summary>
public sealed partial class WebVttSegmenter
{
    [GeneratedRegex(
        @"^(?:(\d+):)?(\d{2}):(\d{2})\.(\d{3})\s+-->\s+(?:(\d+):)?(\d{2}):(\d{2})\.(\d{3})",
        RegexOptions.Compiled,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex TimestampLineRx();

    /// <summary>
    /// Parse <paramref name="vttFilePath"/> and emit one
    /// <see cref="WebVttSegment"/> per segment window up to the last cue end.
    /// </summary>
    public IReadOnlyList<WebVttSegment> Slice(
        string vttFilePath,
        TimeSpan segmentDuration,
        IStorage storage
    )
    {
        if (segmentDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(segmentDuration), "Must be positive.");

        string raw = Encoding.UTF8.GetString(storage.Read(vttFilePath));
        return SliceContent(raw, segmentDuration);
    }

    /// <summary>
    /// Same as <see cref="Slice(string,TimeSpan)"/> but works on an already-
    /// loaded string — used by tests without a real file.
    /// </summary>
    public IReadOnlyList<WebVttSegment> SliceContent(string vttContent, TimeSpan segmentDuration)
    {
        if (segmentDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(segmentDuration), "Must be positive.");

        (string header, List<ParsedCue> cues) = Parse(vttContent);

        if (cues.Count == 0)
            return
            [
                new(0, BuildSegment(header, [], segmentDuration), TimeSpan.Zero, segmentDuration),
            ];

        TimeSpan totalDuration = cues.Max(c => c.End);
        int segmentCount = (int)Math.Ceiling(totalDuration / segmentDuration);
        if (segmentCount == 0)
            segmentCount = 1;

        List<WebVttSegment> result = new(segmentCount);

        for (int i = 0; i < segmentCount; i++)
        {
            TimeSpan segStart = segmentDuration * i;
            TimeSpan segEnd = segmentDuration * (i + 1);

            List<ParsedCue> overlapping =
            [
                .. cues.Where(c => c.Start < segEnd && c.End > segStart),
            ];

            string content = BuildSegment(header, overlapping, segmentDuration);
            result.Add(new(i, content, segStart, segEnd));
        }

        return result;
    }

    // ------------------------------------------------------------------
    // Parsing
    // ------------------------------------------------------------------

    private static (string header, List<ParsedCue> cues) Parse(string vttContent)
    {
        string[] lines = vttContent.ReplaceLineEndings("\n").Split('\n');

        StringBuilder headerSb = new();
        List<ParsedCue> cues = [];

        int idx = 0;

        // First line must be "WEBVTT" (possibly with a description after a space/tab).
        if (idx < lines.Length && lines[idx].StartsWith("WEBVTT", StringComparison.Ordinal))
        {
            headerSb.AppendLine(lines[idx]);
            idx++;
        }

        // Collect header block lines until the first blank line.
        while (idx < lines.Length && !string.IsNullOrWhiteSpace(lines[idx]))
        {
            headerSb.AppendLine(lines[idx]);
            idx++;
        }

        // Parse cue blocks.
        while (idx < lines.Length)
        {
            // Skip blank lines between blocks.
            while (idx < lines.Length && string.IsNullOrWhiteSpace(lines[idx]))
                idx++;

            if (idx >= lines.Length)
                break;

            // Optional cue identifier (not a timestamp line).
            string? cueId = null;
            if (!TimestampLineRx().IsMatch(lines[idx]))
            {
                cueId = lines[idx];
                idx++;
            }

            if (idx >= lines.Length)
                break;

            // Timestamp line.
            if (!TimestampLineRx().IsMatch(lines[idx]))
            {
                idx++;
                continue;
            }

            string timestampLine = lines[idx];
            idx++;

            // Payload lines until blank or EOF.
            StringBuilder payload = new();
            while (idx < lines.Length && !string.IsNullOrWhiteSpace(lines[idx]))
            {
                if (payload.Length > 0)
                    payload.AppendLine();
                payload.Append(lines[idx]);
                idx++;
            }

            if (!TryParseTimestamps(timestampLine, out TimeSpan start, out TimeSpan end))
                continue;

            cues.Add(new(cueId, timestampLine, start, end, payload.ToString()));
        }

        return (headerSb.ToString().TrimEnd(), cues);
    }

    private static bool TryParseTimestamps(string line, out TimeSpan start, out TimeSpan end)
    {
        start = end = TimeSpan.Zero;
        Match m = TimestampLineRx().Match(line);
        if (!m.Success)
            return false;

        start = ParseTs(m, 1);
        end = ParseTs(m, 5);
        return true;
    }

    private static TimeSpan ParseTs(Match m, int hourGroupIndex)
    {
        int hours = m.Groups[hourGroupIndex].Success
            ? int.Parse(m.Groups[hourGroupIndex].Value, CultureInfo.InvariantCulture)
            : 0;
        int minutes = int.Parse(m.Groups[hourGroupIndex + 1].Value, CultureInfo.InvariantCulture);
        int seconds = int.Parse(m.Groups[hourGroupIndex + 2].Value, CultureInfo.InvariantCulture);
        int ms = int.Parse(m.Groups[hourGroupIndex + 3].Value, CultureInfo.InvariantCulture);
        return new(0, hours, minutes, seconds, ms);
    }

    // ------------------------------------------------------------------
    // Output
    // ------------------------------------------------------------------

    private static string BuildSegment(
        string originalHeader,
        List<ParsedCue> cues,
        TimeSpan segmentDuration
    )
    {
        StringBuilder sb = new();

        // If the original header already contains X-TIMESTAMP-MAP leave it;
        // otherwise inject the standard HLS timestamp map.
        bool hasTimestampMap = originalHeader.Contains(
            "X-TIMESTAMP-MAP",
            StringComparison.OrdinalIgnoreCase
        );

        if (!string.IsNullOrWhiteSpace(originalHeader))
        {
            sb.AppendLine(originalHeader);
        }
        else
        {
            sb.AppendLine("WEBVTT");
        }

        if (!hasTimestampMap)
        {
            sb.AppendLine("X-TIMESTAMP-MAP=MPEGTS:0,LOCAL:00:00:00.000");
        }

        foreach (ParsedCue cue in cues)
        {
            sb.AppendLine();
            if (cue.Id is not null)
                sb.AppendLine(cue.Id);
            sb.AppendLine(cue.TimestampLine);
            sb.AppendLine(cue.Payload);
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    // ------------------------------------------------------------------
    // Inner types
    // ------------------------------------------------------------------

    private sealed record ParsedCue(
        string? Id,
        string TimestampLine,
        TimeSpan Start,
        TimeSpan End,
        string Payload
    );
}

/// <summary>
/// One per-segment WebVTT output produced by <see cref="WebVttSegmenter"/>.
/// </summary>
public sealed record WebVttSegment(int Index, string Content, TimeSpan StartTime, TimeSpan EndTime);
