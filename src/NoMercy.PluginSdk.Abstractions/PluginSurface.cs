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

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// Which kind of screen is asking.
///
/// The same three names the components already use, so a plugin branching on the
/// surface and a component hiding itself on one are talking about the same
/// thing. A fourth vocabulary here would mean a plugin could target a surface
/// that no component could hide from.
/// </summary>
public static class PluginSurface
{
    /// <summary>A pointer and a keyboard, with room to spare.</summary>
    public const string Web = "web";

    /// <summary>A thumb on a small screen.</summary>
    public const string Mobile = "mobile";

    /// <summary>A remote at four metres, where nothing is clickable.</summary>
    public const string Tv = "tv";

    /// <summary>Every surface, in the order a dashboard is usually designed.</summary>
    public static readonly string[] All = [Web, Mobile, Tv];

    /// <summary>Whether this is a surface the ecosystem serves.</summary>
    public static bool IsKnown(string? surface)
    {
        return surface is not null && Array.IndexOf(All, surface) >= 0;
    }
}
