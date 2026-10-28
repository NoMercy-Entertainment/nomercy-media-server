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

using System.Text;
using System.Text.RegularExpressions;

namespace NoMercy.Encoder.Subtitles;

/// <summary>
/// Minimal SubRip (.srt) to WebVTT (.vtt) converter for playback-time subtitle
/// downloads. The server only ever serves WebVTT sidecars — player-core parses
/// VTT exclusively — so any acquired SRT candidate is converted before it's
/// written to disk. Not a full SRT parser: it rewrites the millisecond
/// separator on timestamp lines and prefixes the WEBVTT header, which is all
/// SRT-to-VTT requires (cue numbering and payload text are already compatible
/// with WebVTT).
/// </summary>
public static partial class SubtitleFormatConverter
{
    // U+FEFF byte-order mark — some OpenSubtitles-hosted SRT payloads start with one.
    private const char ByteOrderMark = '﻿';

    [GeneratedRegex(@"(\d{2}:\d{2}:\d{2}),(\d{3})", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SrtTimestampCommaRegex();

    /// <summary>
    /// Converts SubRip cue text into a WebVTT document: strips a leading BOM,
    /// normalizes line endings, prefixes the WEBVTT header, and rewrites every
    /// "," millisecond separator on a timestamp line to ".".
    /// </summary>
    public static string SrtToVtt(string srtContent)
    {
        ArgumentNullException.ThrowIfNull(srtContent);

        string normalized = srtContent.TrimStart(ByteOrderMark).ReplaceLineEndings("\n").Trim();
        string body = SrtTimestampCommaRegex().Replace(normalized, "$1.$2");

        StringBuilder sb = new();
        sb.Append("WEBVTT").Append('\n').Append('\n').Append(body).Append('\n');
        return sb.ToString();
    }
}
