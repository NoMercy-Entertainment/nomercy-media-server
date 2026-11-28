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

using NoMercy.Database.Models.Users;

namespace NoMercy.Api.Services.Cast;

/// <summary>
/// Whether a registered TV still has the NoMercy app. A fingerprint alone is not enough:
/// the row keeps it after the app is removed from the TV, and a Cast Connect launch at a
/// TV without the app lands on an empty Web Receiver.
/// </summary>
public static class CastAppPresence
{
    /// <summary>How long after its last device-bus presence a TV still counts as having the app.</summary>
    public static readonly TimeSpan AppSeenWindow = TimeSpan.FromHours(48);

    public static bool HasApp(Device device, DateTime nowUtc) =>
        device.Fingerprint is not null
        && device.WsConnectedAt is { } seenAt
        && nowUtc - seenAt <= AppSeenWindow;
}
