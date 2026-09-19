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
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Api.Plugins;

/// <summary>
/// What a plugin sees when it calls a member contract v3 took away.
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
        // An exception filter has no controller instance, so the plugin route
        // is what identifies one. PluginRouteConvention puts every plugin under
        // a route carrying pluginId and nothing else has that key.
        string? plugin = context.RouteData.Values["pluginId"]?.ToString();
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
            "Plugin {Plugin} called a member contract v3 removed. {What} {Why} {Fix}",
            plugin,
            refusal.What,
            refusal.Why,
            refusal.Fix
        );

        context.Result = new ObjectResult(PluginRefusalDto.From(refusal)) { StatusCode = 501 };
        context.ExceptionHandled = true;
        return Task.CompletedTask;
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
