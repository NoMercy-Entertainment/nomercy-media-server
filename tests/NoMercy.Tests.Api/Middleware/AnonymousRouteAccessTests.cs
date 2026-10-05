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
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Distribution;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Middleware;

/// <summary>
/// Proves AccessLogMiddleware's anonymous gate is truthful: every
/// [AllowAnonymous] route that is NOT already covered by the middleware's
/// hand-kept "ignoredStartsWithRoutes"/"ignoreExact" log-hint lists must still
/// answer a request that carries no bearer token, going through the REAL
/// middleware pipeline (AnonymousNoMercyApiFactory — see
/// NoMercy.Tests.Api.Infrastructure.TestAnonymousAuthHandler, which never
/// produces a principal, unlike the always-authenticated default TestAuthHandler
/// every other fixture uses).
///
/// Each assertion below checks for the route's OWN response (its business logic
/// running), not AccessLogMiddleware's 401 shape (application/problem+json,
/// type "https://nomercy.tv/problems/no-token", authError "NO_TOKEN") — that is
/// the distinction between "the request reached the controller" and "the gate
/// silently ate it", which is exactly what a TestAuthHandler-backed fixture
/// cannot distinguish (see AccessLogMiddlewareTests for the always-authenticated
/// fixture's coverage of the ignore lists and the guid/user checks).
///
/// A companion negative control (<see cref="ProtectedRoute_NoBearer_Returns401FromMiddleware"/>)
/// confirms the fix did not also open a real [Authorize]-protected route.
/// </summary>
[Trait("Category", "Unit")]
public class AnonymousRouteAccessTests : IClassFixture<AnonymousNoMercyApiFactory>
{
    private const string HmacSecret = "test-hmac-secret-anonymous-route-access";

    private readonly AnonymousNoMercyApiFactory _factory;

    public AnonymousRouteAccessTests(AnonymousNoMercyApiFactory factory)
    {
        _factory = factory;
    }

    private HttpClient BuildClientWithDistributedEncoding()
    {
        return _factory
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    // HmacValidationMiddleware and the worker controllers each
                    // resolve their own view of the signing key — the middleware
                    // via IOptions<EncoderOptions>, the controllers via the
                    // EncoderOptions singleton. Override both to the same test
                    // secret so a correctly-signed request clears the HMAC gate.
                    services.RemoveAll<IOptions<EncoderOptions>>();
                    services.AddSingleton<IOptions<EncoderOptions>>(
                        new OptionsWrapper<EncoderOptions>(
                            new() { DistributedEncodingSigningKey = HmacSecret }
                        )
                    );
                    services.RemoveAll<EncoderOptions>();
                    services.AddSingleton(
                        new EncoderOptions { DistributedEncodingSigningKey = HmacSecret }
                    );
                });
            })
            .CreateClient();
    }

    private static async Task AssertNotBlockedByAccessLogMiddlewareAsync(
        HttpResponseMessage response
    )
    {
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return;

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse(body);

        // AccessLogMiddleware's 401 always carries an "authError" extension
        // (NO_TOKEN / INVALID_TOKEN / USER_NOT_FOUND). A controller-level 401
        // (e.g. BaseController.UnauthenticatedResponse) never does.
        bool hasAuthError = json.RootElement.TryGetProperty("authError", out JsonElement authError);
        Assert.False(
            hasAuthError,
            $"AccessLogMiddleware rejected an [AllowAnonymous] route before it reached the controller (authError={(hasAuthError ? authError.GetString() : "n/a")}). Body: {body}"
        );
    }

    // ── the two routes named in the finding ─────────────────────────────

    [Fact]
    public async Task IntakeWebhook_NoBearerToken_ReachesController_NotBlockedByAccessLogMiddleware()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/intake/webhook",
            new { path = "/media/intake/dropped.mkv" }
        );

        await AssertNotBlockedByAccessLogMiddlewareAsync(response);

        // The webhook's OWN token gate must still be the thing that rejects an
        // unauthenticated caller — proving the request reached the controller.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Missing or invalid intake token");
    }

    [Fact]
    public async Task WorkerExecuteTask_NoBearerToken_ValidHmac_ReachesController_NotBlockedByAccessLogMiddleware()
    {
        HttpClient client = BuildClientWithDistributedEncoding();

        byte[] emptyBody = [];
        (long ts, string sig) = Sign("POST", "/api/v1/worker/execute-task", emptyBody);

        HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/worker/execute-task")
        {
            Content = new ByteArrayContent(emptyBody),
        };
        request.Headers.Add("X-NoMercy-Timestamp", ts.ToString());
        request.Headers.Add("X-NoMercy-Signature", sig);

        HttpResponseMessage response = await client.SendAsync(request);

        await AssertNotBlockedByAccessLogMiddlewareAsync(response);

        // The controller's own empty-body guard must be what answers — proving
        // the request reached it rather than dying at the bearer-token gate.
        // The body rides along in the failure message: this has gone 401 twice
        // on CI and never once locally, and the status alone cannot say which
        // gate rejected it — hmac_invalid and the bearer gate look identical.
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "body was {0}", body);
        body.Should().Contain("Empty request body");
    }

    // ── sibling [AllowAnonymous] worker/intake routes (same bug class) ──

    [Fact]
    public async Task WorkerSource_NoBearerToken_ValidHmac_ReachesController_NotBlockedByAccessLogMiddleware()
    {
        HttpClient client = BuildClientWithDistributedEncoding();

        byte[] emptyBody = [];
        (long ts, string sig) = Sign("GET", "/api/v1/worker/source", emptyBody);

        HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/worker/source");
        request.Headers.Add("X-NoMercy-Timestamp", ts.ToString());
        request.Headers.Add("X-NoMercy-Signature", sig);

        HttpResponseMessage response = await client.SendAsync(request);

        await AssertNotBlockedByAccessLogMiddlewareAsync(response);

        // No path/sig query params supplied — ASP.NET's own [FromQuery] required-
        // parameter validation must be what answers (400), proving the request
        // reached model binding for the action rather than dying at the bearer-
        // token gate.
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "body was {0}", body);
        body.Should().Contain("field is required");
    }

    [Fact]
    public async Task WorkerProgress_NoBearerTokenNoHmac_ReachesController_NotBlockedByAccessLogMiddleware()
    {
        // The /progress suffix is exempt from HmacValidationMiddleware by spec
        // (see HmacValidationMiddleware.ExemptSuffixes) — a genuinely bare
        // anonymous request, no headers at all.
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/distribution/workers/worker-1/tasks/task-1/progress",
            new { }
        );

        await AssertNotBlockedByAccessLogMiddlewareAsync(response);

        // Distributed encoding isn't configured on this fixture's default client
        // — the controller's own 503 guard must be what answers.
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    // ── negative control: a real protected route must still 401 ────────

    [Fact]
    public async Task ProtectedRoute_NoBearer_Returns401FromMiddleware()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/v1/home");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static (long Timestamp, string Signature) Sign(string method, string path, byte[] body)
    {
        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        HmacSigner signer = new(HmacSecret);
        string sig = signer.Sign(method, path, ts, body);
        return (ts, sig);
    }
}
