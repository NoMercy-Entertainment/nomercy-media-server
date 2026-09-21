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

using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NoMercy.Authorization;
using NoMercy.Authorization.LiveIngest;
using NoMercy.Database.Models.Users;
using NoMercy.NmSystem.Configuration;

namespace NoMercy.Api.Middleware;

/// <summary>
/// The access gate for library folder paths (<c>/&lt;folderId&gt;/...</c>) that
/// <see cref="DynamicStaticFilesMiddleware"/> serves. Runs after authentication,
/// so it sees the validated principal, and before the file is opened. A request
/// passes only when it is either a loopback self-ingest carrying a key minted for
/// that exact file, or an authenticated, allowed user holding a library grant that
/// covers the folder — the same scope every browse endpoint applies.
/// </summary>
public class FolderAccessMiddleware(
    RequestDelegate next,
    IUserCache userCache,
    ILiveIngestKeyStore ingestKeyStore,
    ILogger<FolderAccessMiddleware> logger
)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (
            !DynamicStaticFilesMiddleware.TryParseFolderId(context.Request.Path, out Ulid folderId)
            || !userCache.FolderIds.Contains(folderId)
        )
        {
            await next(context);
            return;
        }

        if (IsAuthorizedLoopbackIngest(context))
        {
            await next(context);
            return;
        }

        string url = context.Request.Path;

        if (context.User.Identity is not { IsAuthenticated: true })
        {
            logger.LogInformation("Unauthorized request, no jwt: {Url}", url);
            await ProblemResponse.WriteAsync(
                context,
                statusCode: (int)HttpStatusCode.Unauthorized,
                type: "https://nomercy.tv/problems/no-token",
                title: "Authentication required",
                detail: "No valid bearer token was provided. Include a valid JWT in the Authorization header or as an access_token query parameter.",
                authError: "NO_TOKEN"
            );
            return;
        }

        string? claim = context.User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (!Guid.TryParse(claim, out Guid userId) || userId == Guid.Empty)
        {
            logger.LogInformation("Unauthorized request, guid malformed or empty: {Url}", url);
            await ProblemResponse.WriteAsync(
                context,
                statusCode: (int)HttpStatusCode.Forbidden,
                type: "https://nomercy.tv/problems/invalid-token",
                title: "Invalid token",
                detail: "The token subject (sub) is not a valid GUID. The token may be malformed.",
                authError: "INVALID_TOKEN"
            );
            return;
        }

        User? user = userCache.GetUser(userId);

        if (user is null)
        {
            logger.LogInformation("Unauthorized request, user not found: {Url}", url);
            await ProblemResponse.WriteAsync(
                context,
                statusCode: (int)HttpStatusCode.Forbidden,
                type: "https://nomercy.tv/problems/user-not-found",
                title: "User not found",
                detail: "The authenticated user is not registered on this server. Ask the server owner to add your account.",
                authError: "USER_NOT_FOUND"
            );
            return;
        }

        if (!user.Allowed && !user.Owner)
        {
            logger.LogInformation("Unauthorized request, user not allowed: {Url}", url);
            await ProblemResponse.WriteAsync(
                context,
                statusCode: (int)HttpStatusCode.Forbidden,
                type: "https://nomercy.tv/problems/not-allowed",
                title: "Access not granted",
                detail: "Your account has not been granted access to this server's media.",
                authError: "NOT_ALLOWED"
            );
            return;
        }

        if (!userCache.UserMayAccessFolder(userId, folderId))
        {
            logger.LogInformation(
                "Unauthorized request, no library grant for folder {FolderId}: {Url}",
                folderId,
                url
            );
            await ProblemResponse.WriteAsync(
                context,
                statusCode: (int)HttpStatusCode.Forbidden,
                type: "https://nomercy.tv/problems/library-access",
                title: "Library access not granted",
                detail: "Your account has not been granted access to the library this file belongs to.",
                authError: "LIBRARY_ACCESS"
            );
            return;
        }

        await next(context);
    }

    // Loopback self-ingest: ffmpeg/ffprobe pull a library source over the
    // internal serving port with a single-use ingest key scoped to one file,
    // in place of the viewer's bearer. Honoured only for the exact file the
    // key was minted for, and only when the request actually arrived on the
    // loopback-only ingest listener (InternalServerPort + 1). Gating on the
    // OS-bound local port — not just a loopback source IP — closes the case
    // where a local relay (Cloudflare Tunnel's cloudflared) forwards external
    // traffic to the PUBLIC port from 127.0.0.1: that traffic never lands on
    // the ingest port, so it can never reach this bypass.
    private bool IsAuthorizedLoopbackIngest(HttpContext context)
    {
        if (
            context.Connection.LocalPort != RuntimeServerSettings.Current.InternalServerPort + 1
            || context.Connection.RemoteIpAddress is not { } remoteIp
            || !IPAddress.IsLoopback(remoteIp)
        )
            return false;

        string ingestKey = context.Request.Headers["X-NoMercy-Ingest-Key"].ToString();
        return !string.IsNullOrEmpty(ingestKey)
            && ingestKeyStore.TryValidate(ingestKey, context.Request.Path.Value ?? string.Empty);
    }
}
