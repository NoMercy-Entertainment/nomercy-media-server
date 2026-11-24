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

using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging;
using NoMercy.Api.Hubs.Filters;
using NoMercy.Authorization;

namespace NoMercy.Api.Filters;

/// <summary>
/// One request line and one result line for the cast and device HTTP routes. The path is
/// logged without its query string, which can carry an access token.
/// </summary>
public sealed class SessionRequestHttpLogFilter(
    ILogger<SessionRequestHttpLogFilter> logger,
    IUserCache userCache
) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next
    )
    {
        Microsoft.AspNetCore.Http.HttpRequest request = context.HttpContext.Request;
        string method = request.Method;
        string path = request.Path.Value ?? string.Empty;
        string user = UserLogText.Of(context.HttpContext.User.UserId(), userCache);
        string device =
            SessionLogText.SafeIdentifierOrNull(
                context.RouteData.Values.GetValueOrDefault("deviceId")?.ToString()
            ) ?? SessionLogText.Unknown;
        LogLevel level = HttpMethods.IsGet(method) ? LogLevel.Debug : LogLevel.Information;

        logger.Log(
            level,
            "Http request {Method} {Path} user={User} device={Device}",
            method,
            path,
            user,
            device
        );

        Stopwatch stopwatch = Stopwatch.StartNew();
        ActionExecutedContext executed = await next();

        if (executed.Exception is not null && !executed.ExceptionHandled)
        {
            logger.LogError(
                executed.Exception,
                "Http result {Method} {Path} failed exception={Exception} user={User} device={Device} in {Elapsed}ms",
                method,
                path,
                executed.Exception.GetType().Name,
                user,
                device,
                stopwatch.ElapsedMilliseconds
            );
            return;
        }

        int status =
            (executed.Result as IStatusCodeActionResult)?.StatusCode
            ?? context.HttpContext.Response.StatusCode;
        LogLevel resultLevel = status switch
        {
            >= 500 => LogLevel.Error,
            >= 400 => LogLevel.Warning,
            _ => level,
        };
        logger.Log(
            resultLevel,
            "Http result {Method} {Path} status={Status} user={User} device={Device} in {Elapsed}ms",
            method,
            path,
            status,
            user,
            device,
            stopwatch.ElapsedMilliseconds
        );
    }
}
