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

namespace NoMercy.Plugins.Abstractions;

/// <summary>
/// Who is asking. A caller with no access never reaches the plugin: the server
/// answers 403 before dispatch, which is the enforcement point for rule 2.6.1.
/// </summary>
public sealed record PluginCaller(
    UserId Id,
    string DisplayName,
    PluginRole Role,
    PluginAccess Access,
    string Locale,
    string Surface
)
{
    public bool Admits(PluginRouteAccess access)
    {
        return access == PluginRouteAccess.Shared || Role is PluginRole.Owner or PluginRole.Manager;
    }

    public static PluginRefusal RefuseRoute(PluginCaller caller, string route, string plugin)
    {
        return new PluginRefusal(
            PluginRefusalCodes.RouteAccessDenied,
            plugin,
            $"{caller.DisplayName} opened {route}, which the plugin marks owner only.",
            "A route marked owner is for the person who runs the server, not for members or guests.",
            "Mark the route access shared in the route table if members should see it. Docs: /nomercy-plugins/tour/callers-and-access",
            PluginRefusalSeverity.Blocked
        );
    }
}
