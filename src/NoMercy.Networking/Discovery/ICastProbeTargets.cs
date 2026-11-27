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

using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Networking.Cast;

namespace NoMercy.Networking.Discovery;

/// <summary>
/// LAN addresses worth asking for a Cast announcement directly, because a
/// multicast reply from them may never reach this host (Wi-Fi power save, IGMP
/// snooping on a wired-to-wireless bridge).
/// </summary>
public interface ICastProbeTargets
{
    Task<IReadOnlyCollection<string>> GetLanIpsAsync(CancellationToken cancellationToken);
}

/// <summary>Registered devices' LanIp, plus their connect address when it is on this network:
/// a web-receiver TV has no LanIp, and the scanner forgets it on restart.</summary>
public sealed class RegisteredDeviceCastProbeTargets(IDbContextFactory<MediaContext> contextFactory)
    : ICastProbeTargets
{
    public async Task<IReadOnlyCollection<string>> GetLanIpsAsync(
        CancellationToken cancellationToken
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(
            cancellationToken
        );

        List<string?> lanIps = await context
            .Devices.AsNoTracking()
            .Select(d => d.LanIp)
            .ToListAsync(cancellationToken);
        List<string?> connectIps = await context
            .Devices.AsNoTracking()
            .Select(d => (string?)d.Ip)
            .ToListAsync(cancellationToken);

        return
        [
            .. lanIps
                .Concat(connectIps)
                .Where(CastAddress.IsOnThisNetwork)
                .Select(ip => ip!)
                .Distinct(),
        ];
    }
}
