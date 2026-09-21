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

namespace NoMercy.Api.Middleware;

/// <summary>
/// Promotes a <c>?token=</c> / <c>?access_token=</c> query parameter to a bearer
/// Authorization header so the JWT handler validates it like any other request.
/// Runs before authentication; it decides nothing about access. The gate for
/// library folder paths is <see cref="FolderAccessMiddleware"/>, which runs after
/// authentication and can see the validated principal.
/// </summary>
public class TokenParamAuthMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        UseQueryTokenAsBearer(context.Request);
        await next(context);
    }

    /// <summary>
    /// Keeps only the first Authorization value, and takes the JWT from a ?token= or
    /// ?access_token= query parameter when no bearer header was sent.
    /// </summary>
    private static void UseQueryTokenAsBearer(HttpRequest request)
    {
        request.Headers.Authorization = request
            .Headers.Authorization.ToString()
            .Split(",")
            .ElementAt(0)
            .Split("&")
            .ElementAt(0);

        if (request.Headers.Authorization.ToString().Contains("Bearer"))
            return;

        string jwt = request
            .Query.FirstOrDefault(q => q.Key is "token" or "access_token")
            .Value.ToString();

        if (!string.IsNullOrEmpty(jwt))
            request.Headers.Authorization = new("Bearer " + jwt);
    }
}
