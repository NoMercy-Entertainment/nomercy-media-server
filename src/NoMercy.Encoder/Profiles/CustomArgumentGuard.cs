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
using NoMercy.NmSystem.SystemCalls;

namespace NoMercy.Encoder.Profiles;

/// <summary>
///     Denies custom ffmpeg arguments that read or write local files, open extra inputs or reach
///     the network. A deny-list of those categories is used rather than an allow-list: custom
///     arguments are an open escape hatch for any encoder option, and stored profiles must keep
///     encoding. The validator rule, the plan merge points and both command builders share it.
/// </summary>
public static partial class CustomArgumentGuard
{
    private static readonly HashSet<string> DeniedFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "i",
        "progress",
        "vstats",
        "vstats_file",
        "report",
        "dump_attachment",
        "attach",
        "filter_script",
        "filter_complex_script",
        "lavfi",
        "protocol_whitelist",
        "fpre",
        "vpre",
        "apre",
        "spre",
        "sdp_file",
    };

    // Filter and option names that open a file, so "movie=x" or "lut3d=file=x" is refused.
    [GeneratedRegex(
        @"(?<![A-Za-z0-9_])(?:movie|amovie|subtitles|ass|textfile|file|filename|fontfile)\s*=",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex FilterSourcePattern();

    [GeneratedRegex(
        @"^(?:[A-Za-z]:[\\/]|[\\/]|~)",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex AbsolutePathPattern();

    [GeneratedRegex(
        @"(?:^|[\\/])\.\.(?:[\\/]|$)",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex TraversalPattern();

    [GeneratedRegex(
        @"^[A-Za-z][A-Za-z0-9+.\-]+:",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex UrlSchemePattern();

    /// <summary>
    ///     Whether one argument is unsafe. The reason names the category only, never the value.
    ///     <paramref name="checkLocations"/> adds the path and URL value checks; command builders
    ///     leave it off because internal flags (pass-log files, segment names) carry paths.
    /// </summary>
    public static bool TryGetViolation(
        string key,
        string? value,
        bool checkLocations,
        out string reason
    )
    {
        reason = string.Empty;

        string flag = key.Trim().TrimStart('-');
        int specifier = flag.IndexOf(':');
        string flagName = specifier < 0 ? flag : flag[..specifier];

        if (flagName.StartsWith('/') || DeniedFlags.Contains(flagName))
        {
            reason = "denied_flag";
            return true;
        }

        string text = value?.Trim() ?? string.Empty;
        if (text.Length == 0)
            return false;

        if (FilterSourcePattern().IsMatch(text))
        {
            reason = "filter_source";
            return true;
        }

        if (!checkLocations)
            return false;

        if (AbsolutePathPattern().IsMatch(text) || TraversalPattern().IsMatch(text))
        {
            reason = "path_value";
            return true;
        }

        if (UrlSchemePattern().IsMatch(text))
        {
            reason = "url_value";
            return true;
        }

        return false;
    }

    /// <summary>
    ///     True when the argument is unsafe; logs one warning with the key, never the value.
    /// </summary>
    public static bool ShouldDrop(string key, string? value, bool checkLocations, string scope)
    {
        if (!TryGetViolation(key, value, checkLocations, out string reason))
            return false;

        string safeKey = key.ReplaceLineEndings(" ");
        if (safeKey.Length > 64)
            safeKey = safeKey[..64];

        Logger.Warning(
            $"Dropped unsafe ffmpeg custom argument '{safeKey}' ({reason}) from {scope}."
        );
        return true;
    }

    public static Dictionary<string, string> Sanitize(
        IReadOnlyDictionary<string, string> args,
        bool checkLocations,
        string scope
    )
    {
        Dictionary<string, string> kept = [];
        foreach ((string key, string value) in args)
            if (!ShouldDrop(key, value, checkLocations, scope))
                kept[key] = value;

        return kept;
    }
}
