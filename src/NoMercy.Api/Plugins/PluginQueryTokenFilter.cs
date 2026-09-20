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

using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Mvc;

namespace NoMercy.Api.Plugins;

/// <summary>
/// What happens when a caller reaches a plugin route with its bearer token in
/// the URL.
/// <para>
/// Plugin routes ask for a token now, and Internet Radio's audio proxy puts
/// that token in a query string. Refusing it here the day authorization
/// arrived would stop radio playback on a server that works today, which
/// design section 10 item 18 says is premature: v3 ships the replacement
/// capabilities first. So a plugin on ABI 10.x is served and told once, and a
/// plugin on ABI 11 is refused with the teaching message of section 3.9.
/// </para>
/// </summary>
public class PluginQueryTokenFilter(
    IPluginManager pluginManager,
    ILogger<PluginQueryTokenFilter> logger
) : IAsyncActionFilter
{
    private static readonly string[] TokenParameters = ["token", "access_token"];

    // Said once per plugin, not once per request: a radio stream is a request
    // every few seconds, and a warning repeated that often buries the log it
    // was meant to be read in.
    private readonly ConcurrentDictionary<Ulid, byte> _warned = new();

    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.Controller is not PluginControllerBase)
            return next();

        if (!CarriesQueryToken(context.HttpContext.Request))
            return next();

        if (!Ulid.TryParse(context.RouteData.Values["pluginId"]?.ToString(), out Ulid pluginId))
            return next();

        PluginInfo? info = pluginManager.GetPluginInfo(pluginId);

        if (info is null)
            return next();

        if (PluginQueryTokenPolicy.Accepts(info.TargetAbi))
        {
            if (_warned.TryAdd(pluginId, 0))
                logger.LogWarning("{Refusal}", PluginQueryTokenPolicy.Warning(info.Name));

            return next();
        }

        context.Result = new ObjectResult(
            new PluginRefusalDto
            {
                Code = PluginRefusalCode.TokenInUrl,
                Plugin = $"{info.Name} {info.Version}",
                What = PluginQueryTokenPolicy.What(info.Name),
                Why = PluginQueryTokenPolicy.Why,
                Fix = PluginQueryTokenPolicy.Fix,
            }
        )
        {
            StatusCode = StatusCodes.Status401Unauthorized,
        };

        return Task.CompletedTask;
    }

    private static bool CarriesQueryToken(HttpRequest request) =>
        TokenParameters.Any(parameter =>
            !string.IsNullOrEmpty(request.Query[parameter].ToString())
        );
}
