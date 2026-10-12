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

using System.Net.Sockets;

namespace NoMercy.NmSystem.Networking;

/// <summary>
/// Asks something outside this network whether the server's port answers. A probe from
/// inside the LAN cannot tell a router that refuses to hairpin from a port that is closed,
/// and an address on the server's own network card never leaves the machine; a call from
/// the cloud can tell.
/// </summary>
public interface IReachabilityProbe
{
    /// <summary>
    /// Asks the NoMercy API to call this server back over plain http on <paramref name="port"/>
    /// at the public address this request leaves from, forced onto <paramref name="family"/>,
    /// and to expect <paramref name="code"/> as the answer.
    /// </summary>
    Task<ReachabilityVerdict> ProbeBackAsync(
        AddressFamily family,
        int port,
        string code,
        CancellationToken ct
    );
}

/// <summary>
/// Three answers, because "the check itself did not run" must never be read as
/// "closed". An API outage would otherwise switch every server to its fallback.
/// </summary>
public enum ReachabilityVerdict
{
    Reachable,
    Unreachable,
    Unknown,
}
