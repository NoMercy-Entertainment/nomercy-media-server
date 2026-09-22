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
/// Sockets the owner consented to.
/// <para>
/// The torrent plugin wrote a BitTorrent client, DHT, local peer discovery and
/// UPnP against raw BCL types, because the contract offered none of this. Every
/// one of those calls is here instead, behind a capability the owner can see on
/// the permissions page and revoke without uninstalling anything.
/// </para>
/// <para>
/// A host outside the manifest's granted globs refuses with
/// <see cref="PluginRefusalCodes.HostNotAllowed" />, and a plugin that never
/// declared the capability refuses with
/// <see cref="PluginRefusalCodes.SocketUndeclared" />.
/// </para>
/// </summary>
public interface IPluginNet
{
    /// <summary>
    /// An outbound connection. Needs <c>network.dial</c> and a glob that matches
    /// the host.
    /// <para>
    /// The host enforces no read or write timeout on the returned stream, so a
    /// plugin holding many peers sets its own. A peer that never closes holds a
    /// socket until the plugin drops it.
    /// </para>
    /// </summary>
    Task<Stream> DialAsync(
        string host,
        int port,
        PluginTransport transport,
        CancellationToken ct = default
    );

    /// <summary>
    /// A listening socket. Needs <c>network.listen</c> and a port inside the
    /// range the manifest declared. Port zero asks the host to pick one.
    /// </summary>
    Task<IPluginListener> ListenAsync(
        int port,
        PluginTransport transport,
        CancellationToken ct = default
    );

    /// <summary>Finding peers and devices on the owner's own network. Needs <c>network.discover</c>.</summary>
    IPluginNetDiscovery Discovery { get; }

    /// <summary>
    /// Asking the router to forward a port. Travels with <c>network.listen</c>,
    /// because a listener nobody outside can reach is the same plugin failing
    /// quietly rather than loudly.
    /// </summary>
    IPluginPortMap PortMap { get; }
}
