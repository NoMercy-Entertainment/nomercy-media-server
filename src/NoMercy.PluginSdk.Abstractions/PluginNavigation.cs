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
/// Moving between a plugin's own pages.
///
/// A plugin owns everything under its prefix and never writes the prefix down.
/// It says "go to /details/42" and the client resolves that against wherever
/// this plugin happens to live, which is the only way the same tree can work on
/// a web app that mounts it under `/music/plugins/...` and a television that
/// mounts it somewhere else entirely.
///
/// It is also what makes a plugin movable. A mount that changes kind changes
/// every path the plugin sits behind, and a plugin that had written them down
/// would break on a change it never saw.
/// </summary>
public static class PluginNavigation
{
    /// <summary>The key the client reads the destination from.</summary>
    public const string RouteKey = "route";

    // NOTE: PluginActionIntent serialises as type/payload while the NM component
    // contract reads kind/target/body. An action built here and hung on an NM
    // component is therefore not understood by the component that carries it.
    // The contract is the source of truth and PluginActionIntent has to move to
    // it; until then the client reads the contract shape and a plugin action
    // reaches a component only through the navigation helper below.

    /// <summary>Marks a destination as belonging to the plugin rather than the app.</summary>
    public const string RelativeKey = "relative";

    /// <summary>
    /// Go to another page of this plugin. [route] is relative to the plugin,
    /// starting with a slash: "/details/42".
    /// </summary>
    public static PluginActionIntent To(string route)
    {
        if (!route.StartsWith('/'))
            throw new ArgumentException(
                "a plugin route starts with '/', and is relative to the plugin rather than to the app",
                nameof(route)
            );

        return new()
        {
            Type = PluginActionType.Navigate,
            Payload = new() { [RouteKey] = route, [RelativeKey] = true },
        };
    }

    /// <summary>
    /// Go somewhere in the app itself, such as a library page. Absolute, and
    /// deliberately harder to reach for than <see cref="To" />: a plugin
    /// navigating out of its own space is the exception.
    /// </summary>
    public static PluginActionIntent ToApp(string path)
    {
        if (!path.StartsWith('/'))
            throw new ArgumentException("an app path starts with '/'", nameof(path));

        return new()
        {
            Type = PluginActionType.Navigate,
            Payload = new() { [RouteKey] = path, [RelativeKey] = false },
        };
    }

    /// <summary>
    /// Where a navigate action actually points, once the plugin's own prefix is
    /// taken into account. The client calls this rather than reading the payload
    /// itself, so one rule decides it everywhere.
    /// </summary>
    public static string Resolve(PluginActionIntent intent, string kind, Ulid pluginId)
    {
        string route =
            intent.Payload.TryGetValue(RouteKey, out object? value) && value is string text
                ? text
                : "/";

        bool relative =
            !intent.Payload.TryGetValue(RelativeKey, out object? flag) || flag is not false;

        if (!relative)
            return route;

        string prefix = PluginRoutes.PrefixFor(kind, pluginId);

        return route == "/" ? prefix : prefix + route;
    }
}
