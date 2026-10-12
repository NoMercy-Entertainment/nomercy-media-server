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
using NoMercy.NmSystem.Networking;

namespace NoMercy.Networking.Connectivity;

/// <summary>
/// Answers the reachability call-back when the server serves plain http (setup mode, or
/// no certificate yet). With TLS on the port, the plaintext call never reaches the
/// pipeline: <see cref="Certificate.PlaintextToHttpsRedirector"/> answers it instead.
/// Runs first, because setup mode refuses every other route and the check runs exactly
/// while a new server has no certificate.
/// </summary>
public static class ReachCodeMiddleware
{
    public static async Task HandleAsync(HttpContext context, Func<Task> next, ReachCodes codes)
    {
        string? code = codes.Match(context.Request.Path.Value);
        if (code is null)
        {
            await next();
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/plain";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(code);
    }
}
