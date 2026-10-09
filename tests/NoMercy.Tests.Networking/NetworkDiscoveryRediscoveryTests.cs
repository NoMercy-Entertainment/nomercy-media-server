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

using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Networking.Discovery;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Status;
using NoMercy.Tests.Networking.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Networking;

[Trait("Category", "Unit")]
public sealed class NetworkDiscoveryRediscoveryTests
{
    private sealed class StubbedExternalIpDiscovery(InMemoryStorageDriverStub driver)
        : NetworkDiscovery(
            NullLogger<NetworkDiscovery>.Instance,
            driver,
            new AuthTokenStore(),
            new ConnectivityStatus(),
            new()
        )
    {
        internal override Task<string> GetExternalIpAsync() => Task.FromResult("198.51.100.2");
    }

    [Fact]
    public async Task ForceRediscoveryAsync_ExternalIpLookupChanges_UsesFreshValue()
    {
        StubbedExternalIpDiscovery discovery = new(new InMemoryStorageDriverStub());
        discovery.ExternalIp = "198.51.100.1";

        await discovery.ForceRediscoveryAsync();

        Assert.Equal("198.51.100.2", discovery.ExternalIp);
    }

    [Fact]
    public async Task ForceRediscoveryAsync_CachedExternalIpChanges_UsesFreshLookup()
    {
        InMemoryStorageDriverStub driver = new();
        NetworkDiscovery discovery = new(
            NullLogger<NetworkDiscovery>.Instance,
            driver,
            new AuthTokenStore(),
            new ConnectivityStatus(),
            new()
        );
        discovery.ExternalIp = "198.51.100.1";
        discovery.CacheExternalIp("198.51.100.2");

        await discovery.ForceRediscoveryAsync();

        Assert.Equal("198.51.100.2", discovery.ExternalIp);
    }

    [Fact]
    public async Task ForceRediscoveryAsync_ExternalIpOverride_PreservesOverride()
    {
        InMemoryStorageDriverStub driver = new();
        NetworkDiscovery discovery = new(
            NullLogger<NetworkDiscovery>.Instance,
            driver,
            new AuthTokenStore(),
            new ConnectivityStatus(),
            new(),
            externalIpOverride: "198.51.100.1"
        );
        discovery.CacheExternalIp("198.51.100.2");

        await discovery.ForceRediscoveryAsync();

        Assert.Equal("198.51.100.1", discovery.ExternalIp);
    }
}
