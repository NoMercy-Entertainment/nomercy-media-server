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

namespace NoMercy.Plugins.Abstractions;

/// <summary>
/// Port forwarding on the owner's router, through UPnP or NAT-PMP.
/// <para>
/// A mapping is leased rather than permanent, and the host drops every mapping
/// a plugin holds when the plugin stops. A plugin that crashed used to leave a
/// hole in the owner's router that nothing ever closed.
/// </para>
/// </summary>
public interface IPluginPortMap
{
    Task<PluginPortMapping> MapAsync(
        int internalPort,
        int externalPort,
        PluginTransport transport,
        TimeSpan lease,
        CancellationToken ct = default
    );

    Task UnmapAsync(PluginPortMapping mapping, CancellationToken ct = default);

    /// <summary>Every mapping this plugin currently holds, which is what the permissions page shows the owner.</summary>
    Task<IReadOnlyList<PluginPortMapping>> ListAsync(CancellationToken ct = default);
}

/// <param name="ExternalPort">What the router actually granted, which is not always what was asked for.</param>
/// <param name="ExpiresAt">When the lease lapses unless the plugin renews it.</param>
public sealed record PluginPortMapping(
    int InternalPort,
    int ExternalPort,
    PluginTransport Transport,
    DateTimeOffset ExpiresAt
);
