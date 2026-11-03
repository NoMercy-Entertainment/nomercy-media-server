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
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NoMercy.Api.Middleware;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Distribution;
using Xunit;

namespace NoMercy.Tests.Api.Middleware;

/// <summary>
/// The worker's self-registration client signs through <see cref="HmacSigningHandler"/>;
/// these tests send through that handler into the real <see cref="HmacValidationMiddleware"/>,
/// so the signing side and the checking side are proven to agree (#228).
/// </summary>
[Trait("Category", "Unit")]
public class HmacSigningHandlerTests
{
    private const string Secret = "test-hmac-secret-signing-handler";

    [Fact]
    public async Task RegisterRequest_SignedByTheHandler_PassesTheMiddleware()
    {
        using IHost host = StartCoordinator();
        using HttpClient client = BuildWorkerClient(host, tamper: false);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "api/v1/distribution/workers/register",
            new { worker_id = "worker-1", base_url = "https://worker.example" }
        );

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected the signed register request to pass the HMAC middleware, got {(int)response.StatusCode}: {body}"
        );
    }

    [Fact]
    public async Task HeartbeatRequest_SignedByTheHandler_PassesTheMiddleware()
    {
        using IHost host = StartCoordinator();
        using HttpClient client = BuildWorkerClient(host, tamper: false);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "api/v1/distribution/workers/worker-1/heartbeat",
            new { available_cpu_threads = 4 }
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task BodyChangedAfterSigning_IsRejectedByTheMiddleware()
    {
        using IHost host = StartCoordinator();
        using HttpClient client = BuildWorkerClient(host, tamper: true);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "api/v1/distribution/workers/register",
            new { worker_id = "worker-1", base_url = "https://worker.example" }
        );

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("signature_invalid", json.RootElement.GetProperty("reason").GetString());
    }

    private static IHost StartCoordinator()
    {
        IHost host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                    services.AddSingleton<IOptions<EncoderOptions>>(
                        new OptionsWrapper<EncoderOptions>(
                            new() { DistributedEncodingSigningKey = Secret }
                        )
                    )
                );
                web.Configure(app =>
                {
                    app.UseMiddleware<HmacValidationMiddleware>();
                    app.Run(ctx =>
                    {
                        ctx.Response.StatusCode = StatusCodes.Status200OK;
                        return ctx.Response.WriteAsync("ok");
                    });
                });
            })
            .Build();

        host.Start();
        return host;
    }

    private static HttpClient BuildWorkerClient(IHost host, bool tamper)
    {
        TestServer server = host.GetTestServer();
        HttpMessageHandler transport = server.CreateHandler();
        if (tamper)
            transport = new BodyTamperingHandler { InnerHandler = transport };

        HmacSigningHandler signing = new(new() { DistributedEncodingSigningKey = Secret })
        {
            InnerHandler = transport,
        };

        return new(signing) { BaseAddress = server.BaseAddress };
    }

    /// <summary>Swaps the body after the signature was computed, like a man in the middle would.</summary>
    private sealed class BodyTamperingHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            request.Content = new StringContent(
                "{\"worker_id\":\"attacker\",\"base_url\":\"https://evil.example\"}",
                Encoding.UTF8,
                "application/json"
            );
            return base.SendAsync(request, cancellationToken);
        }
    }
}
