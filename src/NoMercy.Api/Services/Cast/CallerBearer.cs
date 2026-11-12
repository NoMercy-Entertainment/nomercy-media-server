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
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.SignalR;

namespace NoMercy.Api.Services.Cast;

/// <summary>
/// The bearer an authenticated request or hub connection presented, read from the
/// connection itself (query <c>access_token</c> first, as the JWT handler does, then
/// the Authorization header). Never read from a request body.
/// </summary>
public static class CallerBearer
{
    private const string Prefix = "Bearer ";

    public static string? From(HubCallerContext? hub) =>
        From(hub?.Features?.Get<IHttpContextFeature>()?.HttpContext);

    public static string? From(HttpContext? http)
    {
        if (http is null)
            return null;

        string? token = http.Request.Query["access_token"].ToString().Split('&')[0];
        if (string.IsNullOrEmpty(token))
            token = http.Request.Headers.Authorization.FirstOrDefault();

        while (token is not null && token.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            token = token[Prefix.Length..];

        return string.IsNullOrWhiteSpace(token) ? null : token;
    }
}
