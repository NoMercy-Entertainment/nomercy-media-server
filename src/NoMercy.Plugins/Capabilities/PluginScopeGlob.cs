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

namespace NoMercy.PluginSdk.Capabilities;

/// <summary>
/// Whether a value falls inside a scope a manifest declared.
/// <para>
/// This lived inside the network handler, so it matched hosts and nothing
/// else. Every other capability with a scope — a folder, a binary, a library —
/// had no matcher at all, which meant either an exact string or nothing, and
/// "nothing" is what most of them did.
/// </para>
/// <para>
/// <c>*</c> matches within one segment and <c>**</c> crosses separators. That
/// distinction is why the two are separate: <c>*.example.com</c> matched
/// <c>a.example.com</c> and not <c>a.b.example.com</c>, and a bare <c>*</c>
/// matched only a value with no separator in it, so there was no way to write
/// "anything" and the only way to reach one host was to leave.
/// </para>
/// </summary>
public static class PluginScopeGlob
{
    // Marked before escaping, with control characters no scope value contains
    // and Regex.Escape leaves alone. Escaping first would turn both glob widths
    // into the same "\*" and the distinction would be gone.
    private const string CrossesSeparators = "\u0001";
    private const string WithinSegment = "\u0002";

    public static Regex ToPattern(string scope, char separator = '.')
    {
        string marked = scope.Replace("**", CrossesSeparators).Replace("*", WithinSegment);

        string escaped = Regex
            .Escape(marked)
            .Replace(CrossesSeparators, ".+")
            .Replace(WithinSegment, $"[^{Regex.Escape(separator.ToString())}]+");

        return new($"^{escaped}$", RegexOptions.IgnoreCase);
    }

    /// <summary>Whether one declared scope covers one value.</summary>
    public static bool Matches(string scope, string value, char separator = '.') =>
        ToPattern(scope, separator).IsMatch(value);

    /// <summary>Whether any declared scope covers one value.</summary>
    public static bool AnyMatches(IEnumerable<string> scopes, string value, char separator = '.') =>
        scopes.Any(scope => Matches(scope, value, separator));
}
