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

namespace NoMercy.PluginSdk.Abstractions;

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

    /// <summary>
    /// Whether a port answers on the server's public address, tested with a
    /// real connection rather than taken from the router's word: a router
    /// that acknowledged a mapping has not proved that it forwards.
    /// <para>
    /// For a port in the range the manifest declares under
    /// <c>network.listen</c>, or the external port of a mapping this plugin
    /// holds; any other port refuses with
    /// <see cref="PluginRefusalCodes.ListenerUndeclared" />. Something has to
    /// be listening on the port for it to read as open.
    /// </para>
    /// <para>
    /// Only <see cref="PluginPortReachability.Closed" /> is a reason to warn
    /// the owner. UDP, a router that does not give its public address, and a
    /// host that does not probe all answer
    /// <see cref="PluginPortReachability.Unknown" />. A router without NAT
    /// loopback can make an open port read as closed from inside the house,
    /// the same way it does for the server's own port. Added in ABI 12.5.
    /// </para>
    /// </summary>
    Task<PluginPortReachability> ProbeAsync(
        int externalPort,
        PluginTransport transport,
        CancellationToken ct = default
    ) => Task.FromResult(PluginPortReachability.Unknown);
}

/// <param name="ExternalPort">What the router actually granted, which is not always what was asked for.</param>
/// <param name="ExpiresAt">When the lease lapses unless the plugin renews it.</param>
public sealed record PluginPortMapping(
    int InternalPort,
    int ExternalPort,
    PluginTransport Transport,
    DateTimeOffset ExpiresAt
);
