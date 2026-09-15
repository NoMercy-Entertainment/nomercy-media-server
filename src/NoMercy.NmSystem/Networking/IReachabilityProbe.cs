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

namespace NoMercy.NmSystem.Networking;

/// <summary>
/// Asks something outside this network whether an address the server is about to
/// advertise actually answers. A probe from inside the LAN cannot tell a router that
/// refuses to hairpin from a port that is closed; a probe from the cloud can.
/// </summary>
public interface IReachabilityProbe
{
    Task<ReachabilityVerdict> ProbeAsync(string url, CancellationToken ct);
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
