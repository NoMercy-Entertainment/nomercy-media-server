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
using NoMercy.PluginSdk.Network;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginNetworkAllowlistHandlerTests
{
    private static HttpClient Client(params string[] hosts) =>
        new(new PluginNetworkAllowlistHandler(hosts) { InnerHandler = new AlwaysOkHandler() });

    [Fact]
    public async Task Allowed_Host_PassesThrough()
    {
        HttpClient client = Client("*.somafm.com");
        HttpResponseMessage response = await client.GetAsync("https://ice1.somafm.com/groovesalad");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Disallowed_Host_Throws()
    {
        HttpClient client = Client("*.somafm.com");
        await Assert.ThrowsAsync<PluginNetworkDeniedException>(() =>
            client.GetAsync("https://evil.example.com/x")
        );
    }

    [Fact]
    public async Task EmptyAllowlist_DeniesEverything()
    {
        HttpClient client = Client();
        await Assert.ThrowsAsync<PluginNetworkDeniedException>(() =>
            client.GetAsync("https://anything.com/")
        );
    }

    [Fact]
    public async Task NullRequestUri_TreatedAsEmptyHost_Denied()
    {
        // HttpClient itself refuses to dispatch a request with a null RequestUri
        // and no BaseAddress, so the only way to exercise the handler's own
        // `request.RequestUri?.Host ?? string.Empty` null-conditional is to call
        // SendAsync directly against a message with RequestUri explicitly unset.
        ExposedAllowlistHandler handler = new(["*.somafm.com"])
        {
            InnerHandler = new AlwaysOkHandler(),
        };
        HttpRequestMessage request = new(HttpMethod.Get, (Uri?)null);

        Func<Task> act = () => handler.InvokeSendAsync(request, CancellationToken.None);

        await Assert.ThrowsAsync<PluginNetworkDeniedException>(act);
    }

    private sealed class ExposedAllowlistHandler(IReadOnlyList<string> allowedHosts)
        : PluginNetworkAllowlistHandler(allowedHosts)
    {
        public Task<HttpResponseMessage> InvokeSendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        ) => SendAsync(request, ct);
    }
}
