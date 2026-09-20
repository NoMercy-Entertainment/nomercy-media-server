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

namespace NoMercy.Plugins.Abstractions;

/// <summary>
/// Everything a plugin serves, in one place.
///
/// The table is the plugin's map of itself. It resolves an incoming path, builds
/// a link to a page by name so a path is never written twice, and reports what
/// exists on a given surface so a client can list pages it can actually open.
/// </summary>
public class PluginRouteTable
{
    private readonly List<PluginRoute> _routes;

    public PluginRouteTable(params PluginRoute[] routes)
    {
        _routes = [.. routes];

        // Two routes claiming one path is a plugin whose behavior depends on
        // declaration order, and whichever lost is a page nobody can reach.
        List<string> duplicates = _routes
            .GroupBy(route => Normalize(route.Path))
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        if (duplicates.Count > 0)
            throw new ArgumentException($"two routes claim '{duplicates[0]}'", nameof(routes));

        List<string> names = _routes
            .GroupBy(route => route.Name)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        if (names.Count > 0)
            throw new ArgumentException($"two routes are named '{names[0]}'", nameof(routes));

        // The host keeps the underscore prefix for pages it adds to every
        // plugin later. A plugin that claimed one would stop working on an
        // upgrade it had nothing to do with, so it is refused now rather than
        // broken then.
        PluginRoute? reserved = _routes.FirstOrDefault(route =>
            Normalize(route.Path).StartsWith('_')
        );

        if (reserved is not null)
            throw new PluginRefusedException(
                PluginRefusalMessages.RouteReservedPrefix(reserved.Name, reserved.Path)
            );
    }

    public IReadOnlyList<PluginRoute> Routes => _routes;

    /// <summary>
    /// The route for a path, with its parameters.
    ///
    /// Literal segments win over parameters, so `/stations/new` reaches the page
    /// of that name rather than being read as a station called "new".
    /// </summary>
    public PluginRouteMatch? Resolve(string path)
    {
        List<PluginRouteMatch> matches = [];

        foreach (PluginRoute route in _routes)
        {
            Dictionary<string, string>? parameters = route.Match(path);
            if (parameters is not null)
                matches.Add(new(route, parameters));
        }

        return matches.OrderBy(match => match.Parameters.Count).FirstOrDefault();
    }

    /// <summary>A path to one of this plugin's pages, by name.</summary>
    public string PathTo(string name, IReadOnlyDictionary<string, string>? parameters = null)
    {
        PluginRoute route =
            _routes.FirstOrDefault(candidate => candidate.Name == name)
            ?? throw new ArgumentException($"no route named '{name}'", nameof(name));

        return route.Build(parameters);
    }

    /// <summary>An action going to one of this plugin's pages, by name.</summary>
    public PluginActionIntent GoTo(
        string name,
        IReadOnlyDictionary<string, string>? parameters = null
    )
    {
        return PluginNavigation.To(PathTo(name, parameters));
    }

    /// <summary>The pages that exist on a surface.</summary>
    public IEnumerable<PluginRoute> On(string surface)
    {
        return _routes.Where(route => route.ExistsOn(surface));
    }

    private static string Normalize(string path)
    {
        // Two routes differing only in what they call a parameter are the same
        // route: `/stations/:id` and `/stations/:slug` both match one path.
        return string.Join(
            '/',
            path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.StartsWith(':') ? ":" : part.ToLowerInvariant())
        );
    }
}

/// <summary>A resolved page and the parameters that got there.</summary>
public record PluginRouteMatch(PluginRoute Route, IReadOnlyDictionary<string, string> Parameters)
{
    /// <summary>One parameter, or null when the route does not take it.</summary>
    public string? Param(string name)
    {
        return Parameters.TryGetValue(name, out string? value) ? value : null;
    }
}
