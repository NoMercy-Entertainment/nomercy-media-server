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
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using NoMercy.Api.Middleware;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Distribution;
using Xunit;

namespace NoMercy.Tests.Api.Middleware;

[Trait("Category", "Unit")]
public class HmacValidationMiddlewareTests
{
    private const string Secret = "test-hmac-secret-middleware";

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds a minimal test pipeline:
    ///   HmacValidationMiddleware → terminal that returns 200 OK.
    /// </summary>
    private static HttpClient BuildClient(string? configuredSecret)
    {
        IHost host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    EncoderOptions opts = new()
                    {
                        DistributedEncodingSigningKey = configuredSecret,
                    };
                    services.AddSingleton<IOptions<EncoderOptions>>(
                        new OptionsWrapper<EncoderOptions>(opts)
                    );
                });
                web.Configure(app =>
                {
                    app.UseMiddleware<HmacValidationMiddleware>();
                    app.Run(ctx =>
                    {
                        ctx.Response.StatusCode = 200;
                        return ctx.Response.WriteAsync("ok");
                    });
                });
            })
            .Build();

        host.Start();
        return host.GetTestClient();
    }

    /// <summary>
    /// Same pipeline as <see cref="BuildClient"/>, plus a registered
    /// <see cref="ILicenseTokenClient"/> — exercises the remote-worker token
    /// introspection branch (Phase 4.9): when a request carries
    /// X-NoMercy-WorkerToken, the middleware checks IntrospectAsync before
    /// falling back to the configured signing key, and the token itself
    /// becomes the HMAC secret when active.
    /// </summary>
    private static HttpClient BuildClientWithWorkerToken(
        string? configuredSecret,
        Mock<ILicenseTokenClient> licenseTokenClient
    )
    {
        IHost host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    EncoderOptions opts = new()
                    {
                        DistributedEncodingSigningKey = configuredSecret,
                    };
                    services.AddSingleton<IOptions<EncoderOptions>>(
                        new OptionsWrapper<EncoderOptions>(opts)
                    );
                    services.AddSingleton(licenseTokenClient.Object);
                });
                web.Configure(app =>
                {
                    app.UseMiddleware<HmacValidationMiddleware>();
                    app.Run(ctx =>
                    {
                        ctx.Response.StatusCode = 200;
                        return ctx.Response.WriteAsync("ok");
                    });
                });
            })
            .Build();

        host.Start();
        return host.GetTestClient();
    }

    private static (long timestamp, string signature) MakeSignature(
        string method,
        string path,
        byte[] body,
        string secret
    )
    {
        HmacSigner signer = new(secret);
        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string sig = signer.Sign(method, path, ts, body);
        return (ts, sig);
    }

    // -------------------------------------------------------------------------
    // Tests: non-protected routes pass through
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("/api/v1/media/movies")]
    [InlineData("/api/v1/auth/login")]
    [InlineData("/health")]
    public async Task NonProtectedRoute_NoHeaders_Returns200(string route)
    {
        HttpClient client = BuildClient(Secret);
        HttpResponseMessage response = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Tests: protected routes without headers → 401
    // -------------------------------------------------------------------------

    // The router reads v1, v1.0 and V1 as the same API version, so every spelling
    // must meet the signature check; /api/v1.0/worker/... used to skip it.
    [Theory]
    [InlineData("/api/v1.0/worker/execute-task")]
    [InlineData("/api/V1/worker/execute-task")]
    [InlineData("/api/v1.0/distribution/workers")]
    [InlineData("/api/v1/worker/./execute-task")]
    public async Task OtherSpellingsOfAProtectedRoute_AreCheckedToo(string path)
    {
        HttpClient client = BuildClient(Secret);
        HttpResponseMessage response = await client.PostAsync(path, new StringContent("{}"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DistributionRoute_NoTimestamp_Returns401()
    {
        HttpClient client = BuildClient(Secret);
        HttpResponseMessage response = await client.GetAsync("/api/v1/distribution/workers");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        JsonDocument json = JsonDocument.Parse(body);
        Assert.Equal("hmac_invalid", json.RootElement.GetProperty("error").GetString());
        Assert.Equal("missing_timestamp", json.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task WorkerRoute_NoSignature_Returns401()
    {
        HttpClient client = BuildClient(Secret);
        HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/worker/execute-task");
        request.Headers.Add(
            "X-NoMercy-Timestamp",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()
        );

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        JsonDocument json = JsonDocument.Parse(body);
        Assert.Equal("missing_signature", json.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task DistributionRoute_WrongSecret_Returns401()
    {
        HttpClient client = BuildClient(Secret);

        byte[] bodyBytes = Encoding.UTF8.GetBytes("{\"test\":true}");
        (long ts, string sig) = MakeSignature(
            "POST",
            "/api/v1/distribution/tasks",
            bodyBytes,
            "wrong-secret"
        );

        HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/distribution/tasks")
        {
            Content = new ByteArrayContent(bodyBytes),
        };
        request.Headers.Add("X-NoMercy-Timestamp", ts.ToString());
        request.Headers.Add("X-NoMercy-Signature", sig);

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        JsonDocument json = JsonDocument.Parse(body);
        Assert.Equal("signature_invalid", json.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task DistributionRoute_StaleTimestamp_Returns401()
    {
        HttpClient client = BuildClient(Secret);

        byte[] bodyBytes = [];
        long staleTs = DateTimeOffset.UtcNow.AddMinutes(-6).ToUnixTimeSeconds();
        HmacSigner signer = new(Secret);
        string sig = signer.Sign("GET", "/api/v1/distribution/workers", staleTs, bodyBytes);

        HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/distribution/workers");
        request.Headers.Add("X-NoMercy-Timestamp", staleTs.ToString());
        request.Headers.Add("X-NoMercy-Signature", sig);

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Tests: happy path → 200
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DistributionRoute_ValidSignature_Returns200()
    {
        HttpClient client = BuildClient(Secret);

        byte[] bodyBytes = Encoding.UTF8.GetBytes("{\"test\":true}");
        (long ts, string sig) = MakeSignature(
            "POST",
            "/api/v1/distribution/tasks",
            bodyBytes,
            Secret
        );

        HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/distribution/tasks")
        {
            Content = new ByteArrayContent(bodyBytes),
        };
        request.Headers.Add("X-NoMercy-Timestamp", ts.ToString());
        request.Headers.Add("X-NoMercy-Signature", sig);

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WorkerRoute_ValidSignature_EmptyBody_Returns200()
    {
        HttpClient client = BuildClient(Secret);

        byte[] bodyBytes = [];
        (long ts, string sig) = MakeSignature(
            "POST",
            "/api/v1/worker/execute-task",
            bodyBytes,
            Secret
        );

        HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/worker/execute-task");
        request.Headers.Add("X-NoMercy-Timestamp", ts.ToString());
        request.Headers.Add("X-NoMercy-Signature", sig);

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Tests: progress-push exemption
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("/api/v1/distribution/workers/worker-1/tasks/task-abc/progress")]
    [InlineData("/api/v1/distribution/workers/xyz/tasks/123/progress")]
    public async Task ProgressPushPath_NoHeaders_IsExempt_Returns200(string path)
    {
        HttpClient client = BuildClient(Secret);
        HttpResponseMessage response = await client.PostAsync(path, new StringContent("{}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Tests: no signing key configured → 503 (misconfigured, not silent pass-through)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task NoSigningKeyConfigured_ProtectedRoute_Returns503()
    {
        // When no signing key is configured, the middleware rejects protected
        // routes with 503 rather than passing through silently — a missing key
        // means distributed encoding is misconfigured, and passing through would
        // bypass HMAC authentication entirely.
        HttpClient client = BuildClient(configuredSecret: null);
        HttpResponseMessage response = await client.GetAsync("/api/v1/distribution/workers");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Tests: remote-worker token introspection (ILicenseTokenClient)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task WorkerToken_ActiveToken_UsesTokenItselfAsHmacSecret()
    {
        const string workerToken = "worker-token-abc123";
        Mock<ILicenseTokenClient> licenseTokenClient = new();
        licenseTokenClient
            .Setup(c => c.IntrospectAsync(workerToken, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntrospectResult(true, [], null));

        // Configured signing key is deliberately different from the worker
        // token — proves the signature must be verified against the WORKER
        // TOKEN, not the static DistributedEncodingSigningKey.
        HttpClient client = BuildClientWithWorkerToken(
            configuredSecret: "static-secret-never-used-here",
            licenseTokenClient
        );

        byte[] bodyBytes = Encoding.UTF8.GetBytes("{\"task\":true}");
        HmacSigner signer = new(workerToken);
        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string sig = signer.Sign("POST", "/api/v1/worker/execute-task", ts, bodyBytes);

        HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/worker/execute-task")
        {
            Content = new ByteArrayContent(bodyBytes),
        };
        request.Headers.Add("X-NoMercy-WorkerToken", workerToken);
        request.Headers.Add("X-NoMercy-Timestamp", ts.ToString());
        request.Headers.Add("X-NoMercy-Signature", sig);

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        licenseTokenClient.Verify(
            c => c.IntrospectAsync(workerToken, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task WorkerToken_ActiveToken_ButSignedWithWrongSecret_Returns401()
    {
        const string workerToken = "worker-token-abc123";
        Mock<ILicenseTokenClient> licenseTokenClient = new();
        licenseTokenClient
            .Setup(c => c.IntrospectAsync(workerToken, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntrospectResult(true, [], null));

        HttpClient client = BuildClientWithWorkerToken(Secret, licenseTokenClient);

        byte[] bodyBytes = [];
        HmacSigner wrongSigner = new("not-the-worker-token");
        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string sig = wrongSigner.Sign("GET", "/api/v1/worker/status", ts, bodyBytes);

        HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/worker/status");
        request.Headers.Add("X-NoMercy-WorkerToken", workerToken);
        request.Headers.Add("X-NoMercy-Timestamp", ts.ToString());
        request.Headers.Add("X-NoMercy-Signature", sig);

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WorkerToken_InactiveToken_Returns401WorkerTokenInvalid()
    {
        const string workerToken = "revoked-worker-token";
        Mock<ILicenseTokenClient> licenseTokenClient = new();
        licenseTokenClient
            .Setup(c => c.IntrospectAsync(workerToken, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntrospectResult(false, [], "Token has been revoked"));

        HttpClient client = BuildClientWithWorkerToken(Secret, licenseTokenClient);

        HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/worker/status");
        request.Headers.Add("X-NoMercy-WorkerToken", workerToken);
        request.Headers.Add(
            "X-NoMercy-Timestamp",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()
        );
        request.Headers.Add("X-NoMercy-Signature", "irrelevant-since-token-is-inactive");

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        JsonDocument json = JsonDocument.Parse(body);
        Assert.Equal("worker_token_invalid", json.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task NoWorkerTokenHeader_FallsBackToConfiguredSigningKey_NeverConsultsLicenseClient()
    {
        Mock<ILicenseTokenClient> licenseTokenClient = new();
        HttpClient client = BuildClientWithWorkerToken(Secret, licenseTokenClient);

        byte[] bodyBytes = Encoding.UTF8.GetBytes("{\"test\":true}");
        (long ts, string sig) = MakeSignature(
            "POST",
            "/api/v1/distribution/tasks",
            bodyBytes,
            Secret
        );

        HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/distribution/tasks")
        {
            Content = new ByteArrayContent(bodyBytes),
        };
        request.Headers.Add("X-NoMercy-Timestamp", ts.ToString());
        request.Headers.Add("X-NoMercy-Signature", sig);

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        licenseTokenClient.Verify(
            c => c.IntrospectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }
}
