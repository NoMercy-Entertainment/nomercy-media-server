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

using NoMercy.NmSystem.Dto;

namespace NoMercy.NmSystem.Status;

public interface IConnectivityStatus
{
    NatStatus NatStatus { get; set; }
    bool PortForwarded { get; set; }
    string? StunPublicIp { get; set; }
    int? StunPublicPort { get; set; }

    string? CloudflareTunnelToken { get; set; }
    TunnelAvailability TunnelAvailability { get; set; }

    /// <summary>
    /// The transport the manager last settled on, in the API's vocabulary:
    /// port_forward, tunnel or local. Reported to the control plane so it publishes
    /// the address clients should actually use.
    /// </summary>
    string Transport { get; set; }
}

public class ConnectivityStatus : IConnectivityStatus
{
    public NatStatus NatStatus { get; set; } = NatStatus.None;
    public bool PortForwarded { get; set; }
    public string? StunPublicIp { get; set; }
    public int? StunPublicPort { get; set; }

    public string? CloudflareTunnelToken { get; set; }
    public TunnelAvailability TunnelAvailability { get; set; } = TunnelAvailability.Unknown;
    public string Transport { get; set; } = "local";
}
