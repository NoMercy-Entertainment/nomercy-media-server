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
using NoMercy.Providers.Abstractions;
using NoMercy.Providers.Lrclib.Models;
using NoMercy.Providers.NoMercy.Models;

namespace NoMercy.Providers.Lrclib.Client;

public partial class LrclibClient : LrclibBaseClient
{
    /// <summary>
    /// Exact signature lookup (<c>/api/get</c>). Returns a single release matched
    /// on artist + track + album + duration, or null when none exists.
    /// </summary>
    public async Task<LrclibSongResult?> Get(
        string[] artists,
        string trackName,
        string? albumName = null,
        double? duration = null,
        bool priority = false
    )
    {
        Dictionary<string, string?> additionalArguments = new()
        {
            { "artist_name", string.Join(",", artists) },
            { "track_name", trackName },
        };
        if (albumName != null)
            additionalArguments.Add("album_name", albumName);
        if (duration.HasValue)
            additionalArguments.Add(
                "duration",
                duration.Value.ToString(CultureInfo.InvariantCulture)
            );

        LrclibSongResult? result = await Get<LrclibSongResult>(
            "get",
            additionalArguments,
            priority
        );
        if (
            !string.IsNullOrEmpty(result?.Message)
            || result?.StatusCode != 200
            || result.Name == "TrackNotFound"
        )
            return null;
        return result;
    }

    /// <summary>
    /// Fuzzy search (<c>/api/search</c>). Returns every candidate release so the
    /// caller can score them and pick the one whose duration matches the local
    /// file, instead of blindly taking the first hit.
    /// </summary>
    public async Task<LrclibSongResult[]?> Search(
        string[] artists,
        string trackName,
        string? albumName = null,
        bool priority = false
    )
    {
        Dictionary<string, string?> additionalArguments = new() { { "track_name", trackName } };
        string artistName = string.Join(",", artists);
        if (!string.IsNullOrEmpty(artistName))
            additionalArguments.Add("artist_name", artistName);
        if (albumName != null)
            additionalArguments.Add("album_name", albumName);

        return await Get<LrclibSongResult[]>("search", additionalArguments, priority);
    }

    /// <summary>
    /// Reduces a raw Lrclib release to a scoreable candidate, preferring synced
    /// lyrics over plain. Returns null when the release has no usable lyrics.
    /// </summary>
    public static LyricCandidate? ToCandidate(LrclibSongResult result)
    {
        if (result.Instrumental)
            return null;

        bool hasSynced = !string.IsNullOrEmpty(result.SyncedLyrics);
        LyricLine[]? lines = ConvertToMusixmatchLyrics(
            hasSynced ? result.SyncedLyrics : result.PlainLyrics,
            hasSynced
        );
        if (lines is null)
            return null;

        return new(
            result.TrackName,
            result.ArtistName,
            result.Duration > 0 ? (int)Math.Round(result.Duration) : null,
            hasSynced,
            lines
        );
    }

    private static LyricLine[]? ConvertToMusixmatchLyrics(string? lyrics, bool synced)
    {
        if (string.IsNullOrEmpty(lyrics))
            return null;
        string[] lines = lyrics.Split(['\r', '\n'], StringSplitOptions.None);

        List<LyricLine> lyricLines = [];
        foreach (string line in lines)
        {
            string trimmedLine = line.Trim();
            if (string.IsNullOrEmpty(trimmedLine))
                continue;
            // LRC headers ([ar:], [ti:], [offset:], ...) describe the file, not the
            // song. Plain lyrics carry no headers, so a bracketed label there is text.
            if (synced && IdTag().IsMatch(trimmedLine))
                continue;
            lyricLines.AddRange(MakeLyricLines(trimmedLine));
        }
        if (lyricLines.Count == 0)
            return null;

        // A repeated tag yields its line once per timestamp, so the lines no longer
        // arrive in play order by themselves.
        return lyricLines.OrderBy(lyricLine => lyricLine.Time.Total).ToArray();
    }

    private static IEnumerable<LyricLine> MakeLyricLines(string trimmedLine)
    {
        MatchCollection timestamps = LeadingTimestamps().Matches(trimmedLine);
        if (timestamps.Count == 0)
        {
            yield return new() { Text = trimmedLine, Time = new() };
            yield break;
        }

        Match lastTimestamp = timestamps[^1];
        string text = trimmedLine[(lastTimestamp.Index + lastTimestamp.Length)..].Trim();
        foreach (Match timestamp in timestamps)
            yield return MakeLyricLine(timestamp, text);
    }

    private static LyricLine MakeLyricLine(Match timestamp, string text)
    {
        int minutes = int.Parse(timestamp.Groups[1].Value);
        int seconds = int.Parse(timestamp.Groups[2].Value);
        // LRC fractions come in both hundredths ([mm:ss.xx]) and milliseconds
        // ([mm:ss.xxx]) precision. Scale by the digit count so a 3-digit tag is
        // read as milliseconds, not as an out-of-range hundredths value.
        string fraction = timestamp.Groups[3].Value;
        double fractionalSeconds = fraction.Length switch
        {
            3 => int.Parse(fraction) / 1000.0,
            2 => int.Parse(fraction) / 100.0,
            _ => 0,
        };
        double total = (minutes * 60) + seconds + fractionalSeconds;

        return new()
        {
            Text = text,
            Time = new()
            {
                Total = total,
                Minutes = minutes,
                Seconds = seconds,
                Hundredths = (int)Math.Round(fractionalSeconds * 100),
            },
        };
    }

    // \G chains each match to the end of the previous one, so only the run of
    // tags at the start of the line is read; whatever follows is the lyric text.
    [GeneratedRegex(@"\G\s*\[(\d{1,2}):(\d{2})(?:[.:](\d{2,3}))?\]")]
    private static partial Regex LeadingTimestamps();

    [GeneratedRegex(@"^\[[A-Za-z#][A-Za-z0-9_#]*:[^\]]*\]$")]
    private static partial Regex IdTag();
}
