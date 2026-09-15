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

namespace NoMercy.NmSystem.Dto;

/// <summary>
/// How remote access should be reached. <see cref="Auto"/> tries every transport in order;
/// anything else pins the server to that one transport and skips the rest, which is the
/// supported way to say "use the tunnel, stop trying to port forward" without editing code.
/// </summary>
public enum ConnectivityMode
{
    Auto,
    PortForward,
    CloudflareTunnel,
    QuickTunnel,
    LocalOnly,
}
