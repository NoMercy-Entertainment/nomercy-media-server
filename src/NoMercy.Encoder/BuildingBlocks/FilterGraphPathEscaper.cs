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

namespace NoMercy.Encoder.BuildingBlocks;

/// <summary>
/// Escapes a filesystem path for safe use as an FFmpeg filtergraph option
/// value (e.g. <c>lut3d=</c>, <c>ass=</c>, <c>subtitles=</c>), then wraps it
/// in single quotes so the graph-level delimiters (<c>,</c> <c>;</c> <c>[</c>
/// <c>]</c> and whitespace) are treated as literal content rather than
/// filtergraph syntax.
///
/// <para>ffmpeg runs the same tokenizer (<c>av_get_token</c>) over a filter's
/// option string twice: once at the graph level, where <c>[ ] , ;</c> and
/// whitespace delimit filters and pads, and once at the option level, where
/// <c>:</c> separates options and <c>=</c> separates a key from its value.
/// Each pass strips one level of single quotes and backslash escapes, and
/// neither pass processes escapes inside single quotes. The value is
/// therefore single-quoted for the graph level (which keeps the backslashes
/// below untouched) and backslash-escaped for the option level: <c>\</c>,
/// <c>:</c> and <c>=</c> become <c>\\</c>, <c>\:</c> and <c>\=</c>.</para>
///
/// <para>An apostrophe is the exception: it cannot be escaped inside the
/// quotes (the quote simply closes there and the rest of the filter chain
/// is swallowed into the path), so the value closes its quote, emits
/// <c>\\\'</c> — which the graph level reduces to <c>\'</c> and the option
/// level to <c>'</c> — and reopens: <c>/it's.ass</c> becomes
/// <c>'/it'\\\''s.ass'</c>.</para>
///
/// <para>Verified empirically against a real ffmpeg build: quoting alone is
/// NOT sufficient — an un-escaped drive-letter colon inside a quoted value
/// makes ffmpeg's <c>subtitles=</c> option parser misread the option
/// boundary entirely (it reports a spurious <c>original_size</c> parse
/// failure). Escaping the same value WITHOUT the surrounding quotes is
/// equally broken — the graph-level comma/semicolon/bracket delimiters
/// desync the option chain. An apostrophe escaped for one level only
/// (<c>'\''</c>) is re-read as a quote by the option level and swallows the
/// following <c>:si=</c> / <c>:fontsdir=</c> into the file name, and an
/// unescaped <c>=</c> in a path that starts with key characters (every
/// Linux absolute path) is parsed as <c>key=value</c> and rejected as an
/// unknown option.</para>
///
/// <para>This is the single shared implementation for every call site that
/// splices a user-controlled path into a filtergraph. Do not duplicate this
/// logic locally.</para>
/// </summary>
public static class FilterGraphPathEscaper
{
    /// <summary>
    /// Normalises Windows separators to forward slashes, escapes the
    /// option-level metacharacters <c>\</c>, <c>:</c>, <c>=</c> and <c>'</c>,
    /// then wraps the result in single quotes ready to splice directly after
    /// a filter's <c>=</c>.
    /// </summary>
    public static string Escape(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        // Normalise Windows separators first so the backslash-doubling
        // pass only sees intentional backslashes (none on a pure path).
        string normalised = path.Replace('\\', '/');

        // Order is critical: the backslash pass must run first so the
        // backslashes the later passes add are not doubled, and the
        // apostrophe pass must run last because its replacement contains
        // backslashes and quotes of its own.
        string escaped = normalised
            .Replace("\\", @"\\") // \ -> \\ (no-op on a normalised path)
            .Replace(":", @"\:") // : -> \: (option separator / drive letter)
            .Replace("=", @"\=") // = -> \= (key/value separator)
            .Replace("'", @"'\\\''"); // ' -> close quote, \\\', reopen quote

        return $"'{escaped}'";
    }
}
