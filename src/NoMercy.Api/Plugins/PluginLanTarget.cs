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

using Microsoft.AspNetCore.Routing;

namespace NoMercy.Api.Plugins;

/// <summary>The plugin action a LAN request resolved to, and the values its path filled in.</summary>
/// <param name="Endpoint">The plugin's own GET action, served by the server's MVC as any other.</param>
/// <param name="Route">The manifest route the path matched.</param>
public sealed record PluginLanTarget(
    RouteEndpoint Endpoint,
    string Route,
    IReadOnlyDictionary<string, string> Values
);
