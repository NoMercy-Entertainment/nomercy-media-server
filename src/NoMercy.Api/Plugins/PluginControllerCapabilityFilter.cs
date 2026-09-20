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
using Microsoft.AspNetCore.Mvc.Filters;
using NoMercy.Api.DTOs.Common;
using NoMercy.Authorization;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Access;
using NoMercy.Plugins.Mvc;

namespace NoMercy.Api.Plugins;

/// <summary>
/// The one place a plugin's REST surface is allowed or refused.
/// <para>
/// Detaching the application part on disable already removes the routes, but
/// the descriptor cache is refreshed asynchronously and a request in flight can
/// still land on a stale descriptor. This check runs per request against
/// current state, so the window is closed rather than narrowed.
/// </para>
/// <para>
/// A plugin that is not installed, not running, or serves no REST is a 404:
/// whether a plugin is here is not something to let a caller probe for. A
/// plugin that is here and is not shared with this account is a 403 carrying
/// the same refusal its page gives, because the caller already knows it exists
/// from the listing and a bare 404 would send them looking for a bug.
/// </para>
/// </summary>
public class PluginControllerCapabilityFilter(
    IPluginManager pluginManager,
    IPluginAccessResolver accessResolver
) : IAsyncActionFilter
{
    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.Controller is not PluginControllerBase)
            return next();

        if (!Ulid.TryParse(context.RouteData.Values["pluginId"]?.ToString(), out Ulid pluginId))
        {
            context.Result = new NotFoundResult();
            return Task.CompletedTask;
        }

        PluginInfo? info = pluginManager.GetPluginInfo(pluginId);

        if (info is null || info.Status != PluginStatus.Active || info.Capabilities?.Rest != true)
        {
            context.Result = new NotFoundResult();
            return Task.CompletedTask;
        }

        // A surface the manifest declares open has no account to resolve
        // access for. Asking anyway refuses every caller, which is an open
        // surface that is shut.
        if (info.Capabilities.RestAnonymous)
            return next();

        if (
            accessResolver.Resolve(pluginId, context.HttpContext.User.UserId()) == PluginAccess.None
        )
        {
            context.Result = new ObjectResult(
                new DataResponseDto<PluginRefusal> { Data = PluginAccessRefusal.For(pluginId) }
            )
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };

            return Task.CompletedTask;
        }

        return next();
    }
}
