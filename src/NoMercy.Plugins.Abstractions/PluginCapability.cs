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
/// The capabilities a host can offer. A name here is a promise about meaning,
/// not about which subsystem answers it.
/// </summary>
public static class PluginCapability
{
    /// <summary>Playback: what is playing, and driving it.</summary>
    public const string Player = "player";

    /// <summary>Where playback is going, and moving it.</summary>
    public const string Cast = "cast";

    /// <summary>Fetching media for offline use.</summary>
    public const string Downloads = "downloads";

    /// <summary>Telling a viewer something.</summary>
    public const string Notifications = "notifications";

    /// <summary>Scanning, matching and organising.</summary>
    public const string Library = "library";

    /// <summary>Work that runs on a schedule.</summary>
    public const string Tasks = "tasks";

    public static readonly string[] All = [Player, Cast, Downloads, Notifications, Library, Tasks];

    public static bool IsKnown(string? capability)
    {
        return capability is not null && Array.IndexOf(All, capability) >= 0;
    }

    /// <summary>
    /// The grant that gates a capability.
    ///
    /// Derived rather than listed, so a new capability cannot be added and left
    /// ungated by someone who forgot the second list.
    /// </summary>
    public static string GrantFor(string capability)
    {
        return $"capability.{capability}";
    }
}
