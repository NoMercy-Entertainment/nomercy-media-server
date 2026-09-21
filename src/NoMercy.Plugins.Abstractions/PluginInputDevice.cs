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
/// How the caller is driving this surface.
///
/// A form that works with a mouse is unusable with a remote, so the view
/// declares what it needs and the client says what it has. Neither guesses
/// from the screen size: a phone on a television stand is still touch, and a
/// browser on a television is still a remote.
/// </summary>
public static class PluginInputDevice
{
    public const string Pointer = "pointer";
    public const string Touch = "touch";
    public const string Remote = "remote";

    public static IReadOnlyList<string> All { get; } = [Pointer, Touch, Remote];

    public static bool IsKnown(string? device)
    {
        return device is not null && All.Contains(device);
    }
}
