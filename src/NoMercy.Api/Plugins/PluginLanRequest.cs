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
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Lan;
using NoMercy.PluginSdk.Mvc;

namespace NoMercy.Api.Plugins;

/// <summary>
/// What of a LAN device's request reaches the plugin.
/// <para>
/// The plugin sees the device, never a user. Whatever the device sent that
/// could stand for an account (a bearer header, a cookie, a token in the
/// query) is taken off before the plugin runs, and the request reads as one
/// nobody signed in to. The credential in the address does not reach the
/// plugin through the path either: the plugin sees its own route, as if the
/// device had asked for it directly.
/// </para>
/// </summary>
public static class PluginLanRequest
{
    private static readonly string[] AccountHeaders =
    [
        "Authorization",
        "Cookie",
        "Proxy-Authorization",
        "X-Api-Key",
    ];

    private static readonly string[] TokenParameters = ["token", "access_token"];

    /// <summary>
    /// Whether the address as it arrived on the wire is plain: letters, digits,
    /// <c>. _ ~ - /</c>, no percent-encoding, no empty or dot segment. The
    /// server's own parsing decodes and folds <c>%2e%2e</c> and <c>..</c>
    /// before routing sees them, so the bytes the device actually sent are
    /// checked as well as the path that was routed.
    /// </summary>
    public static bool IsPlainRawTarget(string? rawTarget)
    {
        // Some hosts, such as the test server, do not record one. The routed
        // path is still checked segment by segment.
        if (string.IsNullOrEmpty(rawTarget))
            return true;

        int query = rawTarget.IndexOf('?');
        string path = query < 0 ? rawTarget : rawTarget[..query];

        if (!path.StartsWith('/'))
            return false;

        string[] segments = path[1..].Split('/');

        return segments.All(PluginLanRoutes.IsPlainSegment);
    }

    /// <summary>
    /// Turns the device's request into the one the plugin's action serves.
    /// </summary>
    /// <param name="pluginPath">The plugin's own address for the route, such as <c>/api/v1/plugins/{id}/hdhr/lineup.json</c>.</param>
    public static void Prepare(
        HttpContext context,
        PluginLanDispatch dispatch,
        Uri baseAddress,
        string pluginPath
    )
    {
        // Nobody is signed in to a LAN request, whatever the device sent.
        context.User = new(new ClaimsIdentity());

        if (context.Features.Get<IAuthenticateResultFeature>() is { } authenticated)
            authenticated.AuthenticateResult = AuthenticateResult.NoResult();

        foreach (string header in AccountHeaders)
            context.Request.Headers.Remove(header);

        context.Request.QueryString = WithoutTokens(context.Request.QueryString);

        context.Request.Path = pluginPath;

        if (context.Features.Get<IHttpRequestFeature>() is { } request)
            request.RawTarget = pluginPath + context.Request.QueryString;

        context.Items.Remove(PluginControllerBase.CallerItemKey);
        context.Items[PluginControllerBase.LanCallerItemKey] = new PluginLanCaller(
            dispatch.DeviceId,
            dispatch.Name,
            baseAddress
        );

        context.Features.Set(dispatch);
    }

    private static QueryString WithoutTokens(QueryString query)
    {
        if (!query.HasValue)
            return query;

        QueryBuilder kept = new();

        foreach ((string key, StringValues values) in QueryHelpers.ParseQuery(query.Value))
        {
            if (TokenParameters.Contains(key, StringComparer.OrdinalIgnoreCase))
                continue;

            foreach (string? value in values)
                kept.Add(key, value ?? string.Empty);
        }

        return kept.ToQueryString();
    }
}
