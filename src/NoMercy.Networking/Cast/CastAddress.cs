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

using System.Net;
using NoMercy.Database.Models.Users;
using NoMercy.Networking.Discovery;
using NoMercy.Networking.Http;

namespace NoMercy.Networking.Cast;

/// <summary>
/// A device row carries two addresses that mean different things. LanIp is written by the
/// mDNS scanner and is where the device actually sits on this network. Ip is where the
/// device last connected FROM, which for anyone reaching the server through the tunnel is
/// their public address — the cast paths all read Ip, so a TV that had been watching from
/// outside sent every LAUNCH at the owner's own router. LanIp is the answer whenever we
/// have one; Ip only stands in while it is an address on this network.
/// </summary>
public static class CastAddress
{
    public static string? Resolve(string? lanIp, string? connectedFromIp)
    {
        if (IsOnThisNetwork(lanIp))
            return lanIp;

        return IsOnThisNetwork(connectedFromIp) ? connectedFromIp : null;
    }

    // LanIp, then Ip, then the one Cast mDNS hit named like the row; two hits is a guess.
    public static string? ResolveForDevice(Device tv, IEnumerable<CastMdnsHit>? seen)
    {
        string? known = Resolve(tv.LanIp, tv.Ip);
        if (known is not null || seen is null)
            return known;

        string?[] names = [tv.CustomName?.Trim(), tv.Name?.Trim()];
        List<CastMdnsHit> matches =
        [
            .. seen.Where(hit =>
                !string.IsNullOrWhiteSpace(hit.FriendlyName)
                && names.Any(name =>
                    !string.IsNullOrEmpty(name)
                    && string.Equals(
                        hit.FriendlyName!.Trim(),
                        name,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
            ),
        ];

        return matches.Count == 1 && IsOnThisNetwork(matches[0].Ip) ? matches[0].Ip : null;
    }

    public static bool IsOnThisNetwork(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
            return false;

        return IPAddress.TryParse(ip, out IPAddress? parsed)
            && ClientIpResolver.IsPrivateNetwork(parsed);
    }
}
