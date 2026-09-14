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

using System.Security.Claims;
using System.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NoMercy.Authorization;
using NoMercy.Database;
using NoMercy.Database.Models.Users;
using NoMercy.Networking.Http;

namespace NoMercy.Api.Middleware;

public class AccessLogMiddleware
{
    private readonly RequestDelegate _next;

    private readonly ILogger<AccessLogMiddleware> _logger;
    private readonly IUserCache _userCache;

    public AccessLogMiddleware(
        RequestDelegate next,
        ILogger<AccessLogMiddleware> logger,
        IUserCache userCache
    )
    {
        _next = next;
        _logger = logger;
        _userCache = userCache;
    }

    // Log-level hints only — NOT an authorization list. Whether a request may
    // proceed without a user is decided below from the matched endpoint's
    // [AllowAnonymous] metadata (or the absence of a matched endpoint, e.g. a
    // static file), mirroring what UseAuthorization already decided upstream.
    // These lists only silence per-request logging for known high-frequency
    // pollers and asset prefixes so they don't flood the console.
    private readonly string[] _ignoredStartsWithRoutes =
    [
        "/images",
        "/swagger",
        "/index",
        "/oauth2",
        "/styles",
        "/scripts",
        "/favicon",
        "/transcode",
        "/manage",
        "/health",
        // Pulled by a firewall on a schedule, with a path token as its only
        // credential — a 401 from here would break the feed.
        "/security/blocklist",
        // SignalR hubs — base path AND /negotiate handshake AND polling
        // fallbacks all share the prefix; exact-match misses /negotiate.
        "/videoHub",
        "/musicHub",
        "/ripperHub",
        "/dashboardHub",
        "/castHub",
    ];

    private readonly string[] _ignoreExact =
    [
        "/",
        "/api/v1/dashboard/logs",
        "/api/v1/dashboard/devices",
        "/api/v1/dashboard/activity",
        // Dashboard overview poll targets — fire every few seconds.
        "/api/v1/dashboard/server",
        "/api/v1/dashboard/server/info",
        "/api/v1/dashboard/server/storage",
        "/api/v1/dashboard/system",
        "/api/v1/dashboard/optical/drives",
        "/api/v1/dashboard/tasks/queue",
        "/api/v1/dashboard/tasks/runners",
        "/api/v1/dashboard/hardware",
        "/api/v1/dashboard/hardware/benchmark",
        // Setup wizard poll targets — fire on every page load.
        "/api/v1/setup/server-info",
        "/api/v1/setup/permissions",
        "/api/v1/setup/libraries",
        "/api/v1/setup/screensaver",
    ];

    private readonly string[] _ignoreIfGuest = ["/status"];

    public async Task InvokeAsync(HttpContext context)
    {
        string path = HttpUtility.UrlDecode(context.Request.Path);

        bool ignoreStart = _ignoredStartsWithRoutes.Any(route =>
            context.Request.Path.ToString().StartsWith(route)
        );

        bool ignoreExactRoute = _ignoreExact.Any(route =>
            context.Request.Path.ToString().Equals(route)
        );

        // Skip logging for file access paths (folder ID prefix)
        bool isFolderPath = _userCache.FolderIds.Any(x =>
            path.StartsWith("/" + x, StringComparison.OrdinalIgnoreCase)
        );

        if (ignoreStart || ignoreExactRoute || isFolderPath)
        {
            await _next(context);
            return;
        }

        // The authoritative anonymous-access decision: an endpoint that carries
        // [AllowAnonymous] metadata (e.g. the intake webhook, the HMAC-authenticated
        // worker routes) — or a request that matched no endpoint at all (static
        // files, served by middleware after this one) — was already let through by
        // UseAuthorization upstream. This middleware must not re-impose a bearer-
        // token requirement those routes were deliberately exempted from; the
        // hand-kept path lists above are diagnostics, not a second authorization gate.
        Endpoint? endpoint = context.GetEndpoint();
        bool isAllowAnonymous =
            endpoint is null || endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;

        bool ignoreIfGuest = _ignoreIfGuest.Any(route =>
            context.Request.Path.ToString().Equals(route)
        );

        string? guid = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(guid, out Guid userId) || userId == Guid.Empty)
        {
            if (isAllowAnonymous || ignoreIfGuest)
            {
                await _next(context);
                return;
            }

            if (guid is null)
                await RejectAsync(
                    context,
                    path,
                    "No GUID",
                    type: "https://nomercy.tv/problems/no-token",
                    title: "Authentication required",
                    detail: "No bearer token was provided. Include a valid JWT in the Authorization header.",
                    authError: "NO_TOKEN"
                );
            else
                await RejectAsync(
                    context,
                    path,
                    "Malformed or empty GUID",
                    type: "https://nomercy.tv/problems/invalid-token",
                    title: "Invalid token",
                    detail: "The token subject (sub) is not a valid GUID. The token may be malformed.",
                    authError: "INVALID_TOKEN"
                );
            return;
        }

        if (isAllowAnonymous)
        {
            await _next(context);
            return;
        }

        User? user = _userCache.Users.FirstOrDefault(x => x.Id.Equals(userId));
        if (user is null)
        {
            // User cache may not be populated yet during startup — try refreshing from DB
            MediaContext mediaContext = context.RequestServices.GetRequiredService<MediaContext>();
            await _userCache.RefreshUsersAsync(mediaContext);
            user = _userCache.Users.FirstOrDefault(x => x.Id.Equals(userId));
        }

        if (user is null)
        {
            await RejectAsync(
                context,
                path,
                "User not found",
                type: "https://nomercy.tv/problems/user-not-found",
                title: "User not found",
                detail: "The authenticated user is not registered on this server. Ask the server owner to add your account.",
                authError: "USER_NOT_FOUND"
            );
            return;
        }

        // Per-request access logging is Debug, not Information: a dashboard or
        // client polls dozens of endpoints every few seconds, so logging every
        // authenticated request at Information floods the console and drowns real
        // events. The whack-a-mole ignore lists above only ever caught the
        // pollers someone noticed. Auth failures above stay at Information — those
        // are rare and worth surfacing.
        _logger.LogDebug("{Name}: {Path}", user.Name, path);

        await _next(context);
    }

    private async Task RejectAsync(
        HttpContext context,
        string path,
        string reason,
        string type,
        string title,
        string detail,
        string authError
    )
    {
        _logger.LogInformation(
            "Unknown: {RemoteIpAddress}: {Path} ({Reason})",
            context.ClientIp(),
            path,
            reason
        );
        await ProblemResponse.WriteAsync(
            context,
            statusCode: 401,
            type: type,
            title: title,
            detail: detail,
            authError: authError
        );
    }
}
