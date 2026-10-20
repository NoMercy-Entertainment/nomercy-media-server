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

using System.Net;
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

    /// <summary>Asks for answers with the QU bit set, so a responder may reply by unicast.</summary>
    void SendUnicastResponseQuery(DomainName service);

    /// <summary>Multicast query from a random port (RFC 6762 legacy): replies come back by unicast to that port,
    /// even where Windows' own mDNS service owns 5353.</summary>
    Task<IReadOnlyList<(Message Reply, string FromIp)>> QueryLegacyMulticastAsync(
        DomainName service,
        CancellationToken cancellationToken
    );

    /// <summary>Sends one query straight to <paramref name="address"/> and returns its reply, or null on timeout.</summary>
    Task<Message?> QueryHostAsync(
        IPAddress address,
        DomainName service,
        CancellationToken cancellationToken
    );
}
