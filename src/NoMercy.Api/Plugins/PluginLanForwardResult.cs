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
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace NoMercy.Api.Plugins;

/// <summary>
/// Runs the plugin's own action for a LAN request that has been checked.
/// <para>
/// The action runs through the endpoint MVC built for it, so every filter
/// that guards a plugin route guards this one too: the capability filter,
/// the token-in-URL filter and the removed-member filter. Only the
/// authorization step is not repeated, because the LAN route is that step
/// for a device.
/// </para>
/// </summary>
public sealed class PluginLanForwardResult(PluginLanTarget target, string version) : IActionResult
{
    public Task ExecuteResultAsync(ActionContext context)
    {
        HttpContext http = context.HttpContext;
        RouteEndpoint endpoint = target.Endpoint;

        RouteValueDictionary values = new(endpoint.RoutePattern.RequiredValues);

        foreach (KeyValuePair<string, object?> fallback in endpoint.RoutePattern.Defaults)
            values.TryAdd(fallback.Key, fallback.Value);

        values["version"] = version;

        foreach ((string name, string value) in target.Values)
            values[name] = value;

        http.SetEndpoint(endpoint);
        http.Request.RouteValues = values;

        RequestDelegate action =
            endpoint.RequestDelegate
            ?? throw new InvalidOperationException(
                $"The plugin endpoint {endpoint.DisplayName} has nothing to run."
            );

        return action(http);
    }
}
