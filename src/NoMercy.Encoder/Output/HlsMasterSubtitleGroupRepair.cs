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

using System.Text.RegularExpressions;

namespace NoMercy.Encoder.Output;

/// <summary>
/// Repairs an HLS master that names a subtitle group on its variants without
/// defining it. Masters written by older builds carry <c>SUBTITLES="subs"</c>
/// on the <c>EXT-X-STREAM-INF</c> line with no
/// <c>EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="subs"</c> entry, and players then
/// invent an unlabelled track for the missing group. <see cref="PlaylistGenerator"/>
/// no longer writes such a master; this fixes the ones already on disk without
/// re-probing any rendition.
/// </summary>
public static partial class HlsMasterSubtitleGroupRepair
{
    private const string StreamInfTag = "#EXT-X-STREAM-INF:";
    private const string MediaTag = "#EXT-X-MEDIA:";
    private const string ClosedCaptionsNone = "CLOSED-CAPTIONS=NONE";

    [GeneratedRegex(
        "(?<=[:,])TYPE=SUBTITLES(?=,|$)",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex SubtitlesTypeAttribute();

    [GeneratedRegex(
        "(?<=[:,])GROUP-ID=\"(?<id>[^\"]*)\"",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex GroupIdAttribute();

    [GeneratedRegex(
        "(?<separator>[:,])SUBTITLES=\"(?<id>[^\"]*)\"(?<trailing>,?)",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex SubtitlesGroupAttribute();

    /// <summary>
    /// Returns <paramref name="master"/> with every <c>SUBTITLES="..."</c>
    /// attribute removed whose group has no subtitle entry, and
    /// <c>CLOSED-CAPTIONS=NONE</c> added to each variant it repaired. Returns
    /// the same instance when nothing dangles, so a caller can skip the write.
    /// </summary>
    public static string DropDanglingSubtitleGroups(string master)
    {
        string[] lines = master.Split('\n');
        HashSet<string> definedGroups = DefinedSubtitleGroups(lines);
        bool changed = false;

        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith(StreamInfTag, StringComparison.Ordinal))
                continue;

            Match reference = SubtitlesGroupAttribute().Match(lines[i]);
            if (!reference.Success || definedGroups.Contains(reference.Groups["id"].Value))
                continue;

            lines[i] = WithoutReference(lines[i], reference);
            changed = true;
        }

        return changed ? string.Join('\n', lines) : master;
    }

    private static HashSet<string> DefinedSubtitleGroups(string[] lines)
    {
        HashSet<string> groups = new(StringComparer.Ordinal);
        foreach (string line in lines)
        {
            string content = line.TrimEnd('\r');
            if (
                !content.StartsWith(MediaTag, StringComparison.Ordinal)
                || !SubtitlesTypeAttribute().IsMatch(content)
            )
                continue;

            Match groupId = GroupIdAttribute().Match(content);
            if (groupId.Success)
                groups.Add(groupId.Groups["id"].Value);
        }

        return groups;
    }

    private static string WithoutReference(string line, Match reference)
    {
        bool hasCarriageReturn = line.EndsWith('\r');
        string content = hasCarriageReturn ? line[..^1] : line;

        // Keep the separator in front of the attribute only when another
        // attribute follows: "...,SUBTITLES="subs"" drops the comma with it,
        // "#EXT-X-STREAM-INF:SUBTITLES="subs",BANDWIDTH=..." keeps the colon.
        string separator = reference.Groups["separator"].Value;
        bool attributeFollows = reference.Groups["trailing"].Length > 0;
        string replacement =
            separator == ":" ? ":"
            : attributeFollows ? ","
            : string.Empty;
        content = content
            .Remove(reference.Index, reference.Length)
            .Insert(reference.Index, replacement);

        if (!content.Contains("CLOSED-CAPTIONS=", StringComparison.Ordinal))
            content += content.EndsWith(':') ? ClosedCaptionsNone : $",{ClosedCaptionsNone}";

        return hasCarriageReturn ? content + '\r' : content;
    }
}
