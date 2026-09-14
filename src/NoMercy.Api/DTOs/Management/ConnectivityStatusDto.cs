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

using Newtonsoft.Json;

namespace NoMercy.Api.DTOs.Management;

public record ConnectivityStatusDto
{
    /// <summary>Starting, Evaluating, DirectAccess, HolePunched, Tunneled or LocalOnly.</summary>
    [JsonProperty("state")]
    public string? State { get; set; }

    /// <summary>Which transport is actually carrying remote traffic.</summary>
    [JsonProperty("transport")]
    public string? Transport { get; set; }

    /// <summary>Auto, or the transport this server has been pinned to.</summary>
    [JsonProperty("mode")]
    public string? Mode { get; set; }

    [JsonProperty("nat_status")]
    public string? NatStatus { get; set; }

    /// <summary>
    /// Whether a tunnel exists for this server, is still being provisioned, or could not be
    /// checked. "Could not be checked" used to be indistinguishable from "you do not have one".
    /// </summary>
    [JsonProperty("tunnel_availability")]
    public string? TunnelAvailability { get; set; }

    [JsonProperty("port_forwarded")]
    public bool PortForwarded { get; set; }
}
