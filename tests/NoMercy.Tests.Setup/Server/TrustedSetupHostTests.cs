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

using Microsoft.AspNetCore.Http;
using NoMercy.Setup.Server;

namespace NoMercy.Tests.Setup.Server;

/// <summary>
/// Requirement: the setup page may only send a browser redirect_uri to Keycloak when
/// it is served from a host Keycloak's redirect allow-list covers: *.nomercy.tv,
/// localhost and the loopback addresses. Every other host (a LAN IP, a NAS name, a
/// look-alike domain) must fall back to the device code flow, so the server never
/// hands Keycloak a redirect to an address it does not own.
/// </summary>
[Trait("Category", "Unit")]
public sealed class TrustedSetupHostTests
{
    [Theory]
    [InlineData("localhost")]
    [InlineData("localhost:7626")]
    [InlineData("LOCALHOST:7626")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:7626")]
    [InlineData("[::1]")]
    [InlineData("[::1]:7626")]
    [InlineData("abc123.nomercy.tv")]
    [InlineData("abc123.nomercy.tv:7626")]
    [InlineData("ABC123.NoMercy.TV:7626")]
    public void IsTrusted_LoopbackAndNoMercyHosts_AreTrusted(string host)
    {
        Assert.True(TrustedSetupHost.IsTrusted(HostString.FromUriComponent(host)));
    }

    [Theory]
    [InlineData("192.0.2.10:7626")]
    [InlineData("nas:7626")]
    [InlineData("nas.local")]
    [InlineData("evilnomercy.tv")]
    [InlineData("nomercy.tv.evil.example")]
    [InlineData("nomercy.tv")]
    [InlineData("127.0.0.1.evil.example")]
    [InlineData("localhost.evil.example")]
    [InlineData("")]
    public void IsTrusted_OtherHosts_AreNotTrusted(string host)
    {
        Assert.False(TrustedSetupHost.IsTrusted(HostString.FromUriComponent(host)));
    }
}
