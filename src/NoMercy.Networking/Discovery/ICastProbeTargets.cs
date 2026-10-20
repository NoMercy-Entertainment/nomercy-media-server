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

/// <summary>Registered devices' last known LAN addresses.</summary>
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

        return await context
            .Devices.AsNoTracking()
            .Where(d => d.LanIp != null && d.LanIp != "")
            .Select(d => d.LanIp!)
            .Distinct()
            .ToListAsync(cancellationToken);
    }
}
