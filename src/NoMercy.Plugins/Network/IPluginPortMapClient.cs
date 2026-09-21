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

using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugins.Network;

/// <summary>
/// The part of port mapping that talks to the router.
/// <para>
/// A router grants the port it chooses, which is not always the one asked for,
/// so mapping answers a number rather than a yes.
/// </para>
/// </summary>
public interface IPluginPortMapClient
{
    /// <summary>The external port the router actually granted.</summary>
    Task<int> MapAsync(
        int internalPort,
        int externalPort,
        PluginTransport transport,
        TimeSpan lease,
        CancellationToken ct = default
    );

    Task UnmapAsync(PluginPortMapping mapping, CancellationToken ct = default);
}
