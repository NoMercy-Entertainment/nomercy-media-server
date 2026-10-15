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
using NoMercy.Authorization;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Api.Plugins;

/// <summary>
/// The caller a plugin is handed, built one way wherever the server hands one
/// over: a page, a search, a hub message. Built in each place by hand, the
/// copies drift, and a plugin then sees the same person as an owner on one
/// surface and a member on another.
/// </summary>
public static class PluginCallers
{
    /// <param name="user">Who is signed in, as the request or connection carries them.</param>
    /// <param name="access">What the access resolver decided for this plugin and this user.</param>
    /// <param name="acceptLanguage">The Accept-Language header, or nothing; English when empty.</param>
    /// <param name="surface">The surface asked for; an unknown one falls back to web.</param>
    public static PluginCaller For(
        ClaimsPrincipal? user,
        PluginAccess access,
        string? acceptLanguage,
        string? surface
    ) =>
        new(
            new UserId(new Ulid(user.UserId())),
            user.UserName(),
            user.Role() == "owner" ? PluginRole.Owner : PluginRole.Member,
            access,
            acceptLanguage is { Length: > 0 } ? acceptLanguage : "en",
            PluginSurface.IsKnown(surface) ? surface! : PluginSurface.Web
        );
}
