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

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Api.Plugins;

/// <summary>
/// What a plugin sees when it calls a member that does not exist in this
/// server's SDK.
/// <para>
/// Removing a member from the contract is a source break for anyone compiling
/// against it and a runtime break for an assembly already compiled. The second
/// one surfaces as a bare <see cref="MissingMemberException" /> the first time
/// the method runs, which says nothing an author can act on and reaches the
/// owner as a 500.
/// </para>
/// <para>
/// The break is allowed. Landing it as a crash is not: it has to name the
/// member, say why it went, and say what replaces it, which is the refusal
/// shape of design section 3.9.
/// </para>
/// </summary>
public class PluginRemovedMemberFilter(ILogger<PluginRemovedMemberFilter> logger)
    : IAsyncExceptionFilter
{
    public Task OnExceptionAsync(ExceptionContext context)
    {
        // An exception filter has no controller instance, so the route is what
        // identifies a plugin. Two routes reach plugin code and they spell it
        // differently: PluginRouteConvention writes pluginId on a plugin's own
        // controllers, and PluginUiController takes id. Keying on one of them
        // misses the other, and the view route is the one a plugin's
        // GetViewAsync runs on.
        string? plugin = PluginOnThisRoute(context);
        if (string.IsNullOrEmpty(plugin))
            return Task.CompletedTask;

        MissingMemberException? missing = Unwrap(context.Exception);
        if (missing is null)
            return Task.CompletedTask;
        PluginRefusal refusal = PluginRefusalMessages.RemovedContractMember(
            plugin,
            missing.Message
        );

        logger.LogError(
            "Plugin {Plugin} called a member that does not exist. {What} {Why} {Fix}",
            plugin,
            refusal.What,
            refusal.Why,
            refusal.Fix
        );

        context.Result = new ObjectResult(PluginRefusalDto.From(refusal)) { StatusCode = 501 };
        context.ExceptionHandled = true;
        return Task.CompletedTask;
    }

    /// <summary>The plugin this route belongs to, under either spelling.</summary>
    private static string? PluginOnThisRoute(ExceptionContext context)
    {
        if (context.RouteData.Values["pluginId"]?.ToString() is { Length: > 0 } fromConvention)
            return fromConvention;

        // Only a route the host serves on a plugin's behalf, never an id that
        // happens to be called id on one of the server's own controllers.
        if (context.ActionDescriptor.DisplayName?.Contains("PluginUiController") != true)
            return null;

        return context.RouteData.Values["id"]?.ToString();
    }

    /// <summary>
    /// The exception a plugin method raises can arrive wrapped, and a missing
    /// member is the innermost thing that went wrong rather than the outermost.
    /// </summary>
    private static MissingMemberException? Unwrap(Exception? exception)
    {
        while (exception is not null)
        {
            if (exception is MissingMemberException missing)
                return missing;

            exception = exception.InnerException;
        }

        return null;
    }
}
