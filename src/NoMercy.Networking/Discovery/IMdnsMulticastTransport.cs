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

using Makaretu.Dns;

namespace NoMercy.Networking.Discovery;

/// <summary>
/// The multicast socket an mDNS scanner listens on. Production joins the
/// 5353 group when <see cref="Start"/> runs; a test passes a transport that
/// never binds, so a test run never asks the Windows firewall.
/// </summary>
public interface IMdnsMulticastTransport : IDisposable
{
    /// <summary>The service a <see cref="ServiceDiscovery"/> is built on. Not started until <see cref="Start"/>.</summary>
    MulticastService Service { get; }

    void Start();

    void Stop();
}
