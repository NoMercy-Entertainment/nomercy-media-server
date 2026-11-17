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

using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Api.Plugins;

/// <summary>
/// Matches the path a device asked for against one route the manifest names.
/// <para>
/// Segment for segment, with no catch-all and nothing optional: the path has
/// exactly as many segments as the route, every plain segment is the same
/// text, and every <c>{name}</c> segment holds one plain value. A path that
/// steps out with <c>..</c>, encodes a character, or has an empty segment
/// matches nothing, because no route can be written to accept it.
/// </para>
/// </summary>
public static class PluginLanRouteTemplate
{
    /// <summary>
    /// The values the path fills in, or null when it is not this route.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? Match(string route, string path)
    {
        string[] routeSegments = route.Split('/');
        string[] pathSegments = path.Split('/');

        if (routeSegments.Length != pathSegments.Length)
            return null;

        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < routeSegments.Length; index++)
        {
            string segment = pathSegments[index];

            if (!PluginLanRoutes.IsPlainSegment(segment))
                return null;

            if (PluginLanRoutes.TryParameter(routeSegments[index], out string name))
            {
                values[name] = segment;
                continue;
            }

            // Case-insensitive, the way the server's own routing reads a path.
            if (!string.Equals(routeSegments[index], segment, StringComparison.OrdinalIgnoreCase))
                return null;
        }

        return values;
    }

    /// <summary>
    /// How many plain segments a route has. Where two routes match one path,
    /// the one with more plain text is the more specific, as in the server's
    /// own routing.
    /// </summary>
    public static int Specificity(string route) =>
        route.Split('/').Count(segment => !PluginLanRoutes.TryParameter(segment, out _));
}
