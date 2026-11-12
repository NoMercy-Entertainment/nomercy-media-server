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
using Microsoft.AspNetCore.Http;
using NoMercy.Api.Controllers.V1;
using Xunit;

namespace NoMercy.Tests.Api;

/// <summary>
/// The proxy sends a TV that advertises "tv-control-token" a freshly minted control
/// token. Every other TV keeps getting the caller's user bearer, as before.
/// </summary>
[Trait("Category", "Unit")]
public class CastProxyControlTokenTests
{
    private const string Host = "203.0.113.20";
    private const int Port = 7626;

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(
                new HttpResponseMessage(status) { Content = new StringContent(body) }
            );
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => throw new HttpRequestException("placeholder: TV unreachable");
    }

    private static HttpContext UserContext()
    {
        DefaultHttpContext context = new();
        context.Request.Method = "POST";
        context.Request.Headers.Authorization = "Bearer user-keycloak-token";
        context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.7");
        return context;
    }

    [Fact]
    public async Task CapableTv_GetsAControlToken_NotTheUserBearer()
    {
        StubHandler handler = new(
            HttpStatusCode.OK,
            """{"api_version":"1.0","auth":["bearer","tv-control-token"]}"""
        );
        using HttpClient client = new(handler);

        string? control = await CastProxyController.ResolveControlTokenAsync(
            client,
            Host,
            Port,
            () => "minted-control-token"
        );
        using HttpRequestMessage forward = CastProxyController.BuildForward(
            UserContext(),
            $"http://{Host}:{Port}/v1/launch",
            control
        );

        Assert.Equal(new Uri($"http://{Host}:{Port}/v1/info"), Assert.Single(handler.Requests));
        Assert.Equal("Bearer minted-control-token", forward.Headers.Authorization!.ToString());
        Assert.DoesNotContain("user-keycloak-token", forward.Headers.Authorization!.ToString());
    }

    [Theory]
    [InlineData("""{"api_version":"1.0"}""")]
    [InlineData("""{"api_version":"1.0","auth":["bearer"]}""")]
    [InlineData("""{"auth":"tv-control-token"}""")]
    [InlineData("not json")]
    public async Task TvWithoutTheCapability_StillGetsTheUserBearer_AndNothingIsMinted(string info)
    {
        using HttpClient client = new(new StubHandler(HttpStatusCode.OK, info));
        int minted = 0;

        string? control = await CastProxyController.ResolveControlTokenAsync(
            client,
            Host,
            Port,
            () =>
            {
                minted++;
                return "minted-control-token";
            }
        );
        using HttpRequestMessage forward = CastProxyController.BuildForward(
            UserContext(),
            $"http://{Host}:{Port}/v1/launch",
            control
        );

        Assert.Null(control);
        Assert.Equal(0, minted);
        Assert.Equal("Bearer user-keycloak-token", forward.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task TvThatAnswersInfoWithAnError_StillGetsTheUserBearer()
    {
        using HttpClient client = new(
            new StubHandler(HttpStatusCode.NotFound, """{"auth":["tv-control-token"]}""")
        );

        string? control = await CastProxyController.ResolveControlTokenAsync(
            client,
            Host,
            Port,
            () => "minted-control-token"
        );

        Assert.Null(control);
    }

    [Fact]
    public async Task TvThatCannotBeProbed_StillGetsTheUserBearer()
    {
        using HttpClient client = new(new ThrowingHandler());

        string? control = await CastProxyController.ResolveControlTokenAsync(
            client,
            Host,
            Port,
            () => "minted-control-token"
        );

        Assert.Null(control);
    }

    [Fact]
    public async Task CapableTv_WithNoTokenToMint_FallsBackToTheUserBearer()
    {
        using HttpClient client = new(
            new StubHandler(HttpStatusCode.OK, """{"auth":["tv-control-token"]}""")
        );

        string? control = await CastProxyController.ResolveControlTokenAsync(
            client,
            Host,
            Port,
            () => null
        );
        using HttpRequestMessage forward = CastProxyController.BuildForward(
            UserContext(),
            $"http://{Host}:{Port}/v1/launch",
            control
        );

        Assert.Equal("Bearer user-keycloak-token", forward.Headers.Authorization!.ToString());
    }
}
