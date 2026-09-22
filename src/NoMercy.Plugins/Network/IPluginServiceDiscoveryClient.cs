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

using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginSdk.Network;

/// <summary>
/// The part of discovery that actually talks to the network, kept behind an
/// interface so the capability checks around it can be tested without a LAN
/// and without waiting for a multicast answer that may never come.
/// </summary>
public interface IPluginServiceDiscoveryClient
{
    IAsyncEnumerable<PluginDiscoveredService> BrowseAsync(
        string protocol,
        CancellationToken ct = default
    );

    Task AnnounceAsync(
        string protocol,
        string instance,
        int port,
        IReadOnlyDictionary<string, string>? attributes,
        CancellationToken ct = default
    );

    Task StopAsync(string protocol, string instance, CancellationToken ct = default);
}
