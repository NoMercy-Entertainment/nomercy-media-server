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

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// What a LAN route in a manifest may say, checked the same way by the server
/// that loads a plugin and by the tool that verifies one before upload.
/// <para>
/// Deliberately narrow. A segment is either plain text or one named value,
/// and both are limited to letters, digits and <c>. _ ~ -</c>. That leaves no
/// way to write a catch-all, an optional part, an encoded character or a
/// <c>..</c>, so a route the owner read in the manifest is the only thing a
/// device can reach, and a request can only ever fill a value inside one
/// segment of it.
/// </para>
/// </summary>
public static class PluginLanRoutes
{
    /// <summary>A handful is what a tuner needs; more is a plugin opening a surface.</summary>
    public const int MaxRoutes = 16;

    public const int MaxRouteLength = 200;

    public const int MaxSegments = 8;

    /// <summary>The longest value a request may put in one named segment.</summary>
    public const int MaxValueLength = 128;

    private const int MaxParameterNameLength = 64;

    /// <summary>
    /// Every reason the manifest's LAN routes cannot be served, or none. An
    /// absent or empty list is no LAN surface at all, which is always fine.
    /// </summary>
    public static IReadOnlyList<string> Problems(PluginCapabilities? capabilities)
    {
        List<string> routes = capabilities?.Lan?.Routes ?? [];

        if (routes.Count == 0)
            return [];

        List<string> problems = [];

        if (capabilities?.Rest != true)
            problems.Add(
                "capabilities.lan.routes names routes, but capabilities.rest is off. A LAN route is one of the plugin's own REST endpoints, so rest must be on."
            );

        if (routes.Count > MaxRoutes)
            problems.Add(
                $"capabilities.lan.routes names {routes.Count} routes; at most {MaxRoutes} are served to devices."
            );

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (string route in routes)
        {
            if (Problem(route) is { } problem)
                problems.Add(problem);
            else if (!seen.Add(route))
                problems.Add($"capabilities.lan.routes names '{route}' twice.");
        }

        return problems;
    }

    /// <summary>Why one route template cannot be served, or null when it can.</summary>
    public static string? Problem(string? route)
    {
        if (string.IsNullOrWhiteSpace(route))
            return "capabilities.lan.routes has an empty entry.";

        if (route.Length > MaxRouteLength)
            return $"The LAN route '{route[..40]}...' is longer than {MaxRouteLength} characters.";

        string[] segments = route.Split('/');

        if (segments.Length > MaxSegments)
            return $"The LAN route '{route}' has more than {MaxSegments} segments.";

        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        foreach (string segment in segments)
        {
            if (segment.Length == 0)
                return $"The LAN route '{route}' has an empty segment. Write it without a leading, trailing or double slash, relative to the plugin's own prefix.";

            if (TryParameter(segment, out string? name))
            {
                if (!names.Add(name))
                    return $"The LAN route '{route}' names the value '{name}' twice.";

                continue;
            }

            if (!IsPlainSegment(segment))
                return $"The LAN route '{route}' has the segment '{segment}'. A segment is plain text of letters, digits and . _ ~ - (not . or ..), or one value written as {{name}}.";
        }

        return null;
    }

    /// <summary>
    /// Plain text a device can send in a path: letters, digits and
    /// <c>. _ ~ -</c>, and never <c>.</c> or <c>..</c> on its own.
    /// </summary>
    public static bool IsPlainSegment(string? segment)
    {
        if (string.IsNullOrEmpty(segment) || segment.Length > MaxValueLength)
            return false;

        if (segment is "." or "..")
            return false;

        foreach (char character in segment)
        {
            if (!IsPlainCharacter(character))
                return false;
        }

        return true;
    }

    /// <summary>
    /// A segment written as <c>{name}</c>: a letter, then letters, digits or
    /// underscores. No constraint, default, optional mark or catch-all.
    /// </summary>
    public static bool TryParameter(string segment, out string name)
    {
        name = string.Empty;

        if (segment.Length < 3 || segment[0] != '{' || segment[^1] != '}')
            return false;

        string inner = segment[1..^1];

        if (inner.Length > MaxParameterNameLength || !char.IsAsciiLetter(inner[0]))
            return false;

        foreach (char character in inner)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '_')
                return false;
        }

        name = inner;
        return true;
    }

    private static bool IsPlainCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '~' or '-';
}
