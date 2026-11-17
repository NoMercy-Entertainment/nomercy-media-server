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

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Mvc;

namespace NoMercy.Api.Plugins;

/// <summary>
/// Finds the plugin action a LAN route names, among the endpoints the server
/// already serves.
/// <para>
/// Not a second router. The plugin's controllers are attached to MVC and
/// prefixed by <see cref="PluginRouteConvention" /> as always; this only
/// picks, out of that one table, the GET action whose own template is the
/// route the manifest names. An action is only ever a candidate when its
/// controller came from this plugin's assembly, so a manifest can never
/// name a route of the server's or of another plugin.
/// </para>
/// </summary>
public class PluginLanEndpoints(EndpointDataSource endpoints, IPluginAssemblyCatalog catalog)
{
    // api / v{version:apiVersion} / plugins / {the plugin's id, as a literal}
    private const int PrefixSegments = 4;

    /// <summary>
    /// The action for <paramref name="path" />, or null when no route the
    /// manifest names matches it, or when the plugin has no single GET action
    /// for that route.
    /// </summary>
    public PluginLanTarget? Find(Ulid pluginId, IReadOnlyList<string> routes, string path)
    {
        List<(string Route, IReadOnlyDictionary<string, string> Values)> matches = [];

        foreach (string route in routes)
        {
            if (PluginLanRouteTemplate.Match(route, path) is { } values)
                matches.Add((route, values));
        }

        if (matches.Count == 0)
            return null;

        int best = matches.Max(match => PluginLanRouteTemplate.Specificity(match.Route));
        List<(string Route, IReadOnlyDictionary<string, string> Values)> chosen =
        [
            .. matches.Where(match => PluginLanRouteTemplate.Specificity(match.Route) == best),
        ];

        // Two routes that read the same path equally well are a manifest that
        // does not say which one it meant. Guessing is how a device ends up on
        // the route the owner did not think it would reach.
        if (chosen.Count != 1)
            return null;

        RouteEndpoint? endpoint = ActionFor(pluginId, chosen[0].Route);

        return endpoint is null ? null : new(endpoint, chosen[0].Route, chosen[0].Values);
    }

    private RouteEndpoint? ActionFor(Ulid pluginId, string route)
    {
        string[] routeSegments = route.Split('/');

        List<RouteEndpoint> candidates =
        [
            .. endpoints
                .Endpoints.OfType<RouteEndpoint>()
                .Where(endpoint =>
                    IsThisPluginsGetAction(endpoint, pluginId)
                    && HasTemplate(endpoint.RoutePattern, pluginId, routeSegments)
                ),
        ];

        return candidates.Count == 1 ? candidates[0] : null;
    }

    private bool IsThisPluginsGetAction(RouteEndpoint endpoint, Ulid pluginId)
    {
        if (endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is not { } action)
            return false;

        if (!typeof(PluginControllerBase).IsAssignableFrom(action.ControllerTypeInfo))
            return false;

        // The assembly decides who owns a controller, never a route value.
        if (catalog.OwnerOf(action.ControllerTypeInfo.Assembly) != pluginId)
            return false;

        // GET named outright. An action that answers any method answers GET
        // too, but it was not written as something a device only reads.
        return endpoint.Metadata.GetMetadata<IHttpMethodMetadata>() is { } methods
            && methods.HttpMethods.Contains(HttpMethods.Get, StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasTemplate(RoutePattern pattern, Ulid pluginId, string[] routeSegments)
    {
        IReadOnlyList<RoutePatternPathSegment> segments = pattern.PathSegments;

        if (segments.Count != PrefixSegments + routeSegments.Length)
            return false;

        if (!IsLiteral(segments[0], "api") || !IsLiteral(segments[2], "plugins"))
            return false;

        if (!IsLiteral(segments[PrefixSegments - 1], pluginId.ToString()))
            return false;

        for (int index = 0; index < routeSegments.Length; index++)
        {
            RoutePatternPathSegment segment = segments[PrefixSegments + index];

            if (!segment.IsSimple)
                return false;

            string written = routeSegments[index];

            bool same = segment.Parts[0] switch
            {
                RoutePatternLiteralPart literal => string.Equals(
                    literal.Content,
                    written,
                    StringComparison.OrdinalIgnoreCase
                ),
                RoutePatternParameterPart parameter => !parameter.IsCatchAll
                    && !parameter.IsOptional
                    && PluginLanRoutes.TryParameter(written, out string name)
                    && string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };

            if (!same)
                return false;
        }

        return true;
    }

    private static bool IsLiteral(RoutePatternPathSegment segment, string text) =>
        segment is { IsSimple: true, Parts: [RoutePatternLiteralPart literal] }
        && string.Equals(literal.Content, text, StringComparison.OrdinalIgnoreCase);
}
