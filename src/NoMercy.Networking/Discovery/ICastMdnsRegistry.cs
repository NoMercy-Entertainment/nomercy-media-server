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

namespace NoMercy.Networking.Discovery;

/// <summary>
/// Read side of <see cref="GoogleCastDeviceScanner"/> — lets a DTO-projection
/// site (DeviceHub, DeviceBusRegistry) ask whether a registered device's own
/// LAN address is currently answering the OS-level Google Cast mDNS query,
/// without depending on the scanner's discovery/lifecycle surface.
/// </summary>
public interface ICastMdnsRegistry
{
    /// <summary>
    /// True when a `_googlecast._tcp` announcement was seen, within the
    /// staleness window, from an IP equal to <paramref name="lanIp"/>.
    /// </summary>
    bool IsReachable(string? lanIp);

    /// <summary>
    /// Every `_googlecast._tcp` hit currently within the staleness window —
    /// every Chromecast, Cast-Connect Android TV, or Cast-enabled speaker
    /// physically present on the LAN right now, regardless of whether it
    /// corresponds to any registered NoMercy device. The caller (DeviceHub,
    /// DeviceBusRegistry) cross-references these against Devices.LanIp to
    /// tell a real NoMercy client apart from a Chromecast with no NoMercy
    /// relationship at all.
    /// </summary>
    IReadOnlyCollection<CastMdnsHit> GetSeen();

    /// <summary>Every hit since start, however old; only the cast wake path reads it.</summary>
    IReadOnlyCollection<CastMdnsHit> GetLastKnown();
}
