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
/// A page and the values it needs, kept apart.
/// <para>
/// A route that travelled as one joined string broke on the first value with a
/// separator in it: a genre called "ambient, chill" became two segments and
/// reached a page that does not exist. The parameters stay parameters until
/// the client builds the link.
/// </para>
/// </summary>
/// <param name="Route">The route's name in the plugin's own table.</param>
/// <param name="Params">Its values, unescaped.</param>
public sealed record PluginRouteRef(string Route, IReadOnlyDictionary<string, string> Params)
{
    public PluginRouteRef(string route)
        : this(route, new Dictionary<string, string>()) { }
}
