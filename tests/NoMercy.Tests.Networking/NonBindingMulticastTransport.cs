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
using NoMercy.Networking.Discovery;

namespace NoMercy.Tests.Networking;

/// <summary>
/// A multicast transport that never joins the 5353 group. The service is
/// created but never started, so no socket opens and no firewall prompt
/// appears. Every test that builds a scanner passes this.
/// </summary>
internal sealed class NonBindingMulticastTransport : IMdnsMulticastTransport
{
    public MulticastService Service { get; } = new();

    public bool Started { get; private set; }

    public List<DomainName> UnicastResponseQueries { get; } = [];

    public List<IPAddress> HostQueries { get; } = [];

    public Dictionary<IPAddress, Message> HostReplies { get; } = [];

    public void Start() => Started = true;

    public void Stop() => Started = false;

    public void SendUnicastResponseQuery(DomainName service) => UnicastResponseQueries.Add(service);

    public Task<Message?> QueryHostAsync(
        IPAddress address,
        DomainName service,
        CancellationToken cancellationToken
    )
    {
        HostQueries.Add(address);
        return Task.FromResult(HostReplies.GetValueOrDefault(address));
    }

    public void Dispose() => Service.Dispose();
}
