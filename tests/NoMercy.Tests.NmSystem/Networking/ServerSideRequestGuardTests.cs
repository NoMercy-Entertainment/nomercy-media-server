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
using NoMercy.NmSystem.Networking;

namespace NoMercy.Tests.NmSystem.Networking;

[Trait("Category", "Unit")]
public class ServerSideRequestGuardTests
{
    [Theory]
    // Publicly routable — allowed.
    [InlineData(["8.8.8.8", true])]
    [InlineData(["1.1.1.1", true])]
    [InlineData(["93.184.216.34", true])]
    [InlineData(["2606:4700:4700::1111", true])]
    // Loopback.
    [InlineData(["127.0.0.1", false])]
    [InlineData(["127.255.255.254", false])]
    [InlineData(["::1", false])]
    // RFC 1918 private.
    [InlineData(["10.0.0.5", false])]
    [InlineData(["172.16.0.1", false])]
    [InlineData(["172.31.255.255", false])]
    [InlineData(["192.168.1.10", false])]
    // Link-local (incl. the cloud metadata address) + CGNAT + unspecified.
    [InlineData(["169.254.169.254", false])]
    [InlineData(["100.64.0.1", false])]
    [InlineData(["0.0.0.0", false])]
    // IPv6 link-local + unique-local.
    [InlineData(["fe80::1", false])]
    [InlineData(["fc00::1", false])]
    [InlineData(["fd12:3456::1", false])]
    public void IsPubliclyRoutable_ClassifiesAddress(string ip, bool expected)
    {
        Assert.Equal(expected, ServerSideRequestGuard.IsPubliclyRoutable(IPAddress.Parse(ip)));
    }

    [Fact]
    public void IsPubliclyRoutable_IPv4MappedPrivateV6_IsRejected()
    {
        // ::ffff:10.0.0.1 must be unwrapped and rejected, not treated as a public v6.
        Assert.False(ServerSideRequestGuard.IsPubliclyRoutable(IPAddress.Parse("::ffff:10.0.0.1")));
    }

    [Theory]
    // Absolute http(s) URL to a public IP literal — allowed (no DNS needed).
    [InlineData(["https://1.1.1.1/openload.srt", true])]
    [InlineData(["http://8.8.8.8/x", true])]
    // The SSRF payloads that motivated the guard.
    [InlineData(["http://169.254.169.254/latest/meta-data/", false])]
    [InlineData(["http://127.0.0.1:7626/api/v1/dashboard/server", false])]
    [InlineData(["https://10.0.0.1/preset.json", false])]
    [InlineData(["http://[::1]/x", false])]
    // Non-http(s) schemes and malformed input.
    [InlineData(["file:///etc/passwd", false])]
    [InlineData(["ftp://1.1.1.1/x", false])]
    [InlineData(["gopher://1.1.1.1/x", false])]
    [InlineData(["not-a-url", false])]
    [InlineData(["/relative/path", false])]
    [InlineData(["", false])]
    public async Task IsSafePublicHttpUrlAsync_ValidatesSchemeAndHost(string url, bool expected)
    {
        Assert.Equal(expected, await ServerSideRequestGuard.IsSafePublicHttpUrlAsync(url));
    }

    [Fact]
    public async Task IsSafePublicHttpUrlAsync_Null_IsRejected()
    {
        Assert.False(await ServerSideRequestGuard.IsSafePublicHttpUrlAsync(null));
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\n")]
    public async Task IsSafePublicHttpUrlAsync_WhitespaceOnly_IsRejected(string url)
    {
        Assert.False(await ServerSideRequestGuard.IsSafePublicHttpUrlAsync(url));
    }

    [Theory]
    [InlineData("172.20.0.1")]
    [InlineData("172.31.0.1")]
    public void IsPubliclyRoutable_172RangePrivate_IsRejected(string ip)
    {
        Assert.False(ServerSideRequestGuard.IsPubliclyRoutable(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("100.127.255.255")]
    [InlineData("100.64.0.0")]
    public void IsPubliclyRoutable_CGNATRange_IsRejected(string ip)
    {
        Assert.False(ServerSideRequestGuard.IsPubliclyRoutable(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("ff00::1")]
    [InlineData("ff02::1")]
    public void IsPubliclyRoutable_IPv6Multicast_IsRejected(string ip)
    {
        Assert.False(ServerSideRequestGuard.IsPubliclyRoutable(IPAddress.Parse(ip)));
    }

    [Fact]
    public void IsPubliclyRoutable_IPv6Any_IsRejected()
    {
        Assert.False(ServerSideRequestGuard.IsPubliclyRoutable(IPAddress.IPv6Any));
    }

    [Fact]
    public void IsPubliclyRoutable_IPv6None_IsRejected()
    {
        Assert.False(ServerSideRequestGuard.IsPubliclyRoutable(IPAddress.IPv6None));
    }

    [Fact]
    public async Task IsSafePublicHttpUrlAsync_MixedCaseScheme_Accepted()
    {
        Assert.True(await ServerSideRequestGuard.IsSafePublicHttpUrlAsync("HTTPS://8.8.8.8/"));
    }

    [Fact]
    public async Task IsSafePublicHttpUrlAsync_WithPort_Accepted()
    {
        Assert.True(
            await ServerSideRequestGuard.IsSafePublicHttpUrlAsync("https://1.1.1.1:443/path")
        );
    }

    [Fact]
    public void IsPubliclyRoutable_PublicIpv6_Accepted()
    {
        Assert.True(
            ServerSideRequestGuard.IsPubliclyRoutable(IPAddress.Parse("2001:4860:4860::8888"))
        );
    }

    // The URL check runs once, on the first URL. A redirect or a second DNS answer
    // reaches the network only through the connect step, so that is where it is refused.
    [Fact]
    public async Task PublicOnlyHandler_RefusesToConnectToLoopback_EvenWithAListenerThere()
    {
        using System.Net.Sockets.TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using HttpClient client = new(ServerSideRequestGuard.CreatePublicOnlyHandler());
        client.Timeout = TimeSpan.FromSeconds(5);

        HttpRequestException refused = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync($"http://127.0.0.1:{port}/manage/status")
        );
        Assert.Contains("not publicly routable", refused.ToString(), StringComparison.Ordinal);
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task PublicOnlyHandler_RefusesAHostNameThatResolvesToLoopback()
    {
        using HttpClient client = new(ServerSideRequestGuard.CreatePublicOnlyHandler());
        client.Timeout = TimeSpan.FromSeconds(5);

        HttpRequestException refused = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync("http://localhost:9/")
        );
        Assert.Contains("not publicly routable", refused.ToString(), StringComparison.Ordinal);
    }
}
