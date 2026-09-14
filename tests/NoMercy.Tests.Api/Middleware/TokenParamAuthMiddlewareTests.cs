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
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Api.Middleware;
using NoMercy.Api.Services;
using NoMercy.Authorization;
using NoMercy.Authorization.LiveIngest;
using NoMercy.NmSystem.Configuration;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Middleware;

/// <summary>
/// Coverage for TokenParamAuthMiddleware: the loopback self-ingest bypass (gated
/// on the exact internal ingest port, not just a loopback source IP), the
/// ?token=/?access_token= query-param-to-Authorization-header promotion, and the
/// folder-scoped 401/403 problem responses for static file access under a
/// library folder id. Reuses NoMercyApiFactory purely for its already-seeded
/// UserCache (real folder ids, real seeded user) — no HTTP request is made
/// through the factory itself.
/// </summary>
[Trait("Category", "Unit")]
public class TokenParamAuthMiddlewareTests : IClassFixture<NoMercyApiFactory>
{
    public TokenParamAuthMiddlewareTests(NoMercyApiFactory factory)
    {
        factory.CreateClient();
    }

    private static TokenParamAuthMiddleware CreateMiddleware(
        Mock<ILiveIngestKeyStore> ingestKeyStore,
        out StrongBox<bool> nextCalled
    )
    {
        StrongBox<bool> called = new(false);
        TokenParamAuthMiddleware middleware = new(
            _ =>
            {
                called.Value = true;
                return Task.CompletedTask;
            },
            ingestKeyStore.Object,
            NullLogger<TokenParamAuthMiddleware>.Instance,
            UserCache.Current
        );
        nextCalled = called;
        return middleware;
    }

    private static DefaultHttpContext MakeContext(
        string path,
        IPAddress? remoteIp = null,
        int? localPort = null,
        ClaimsPrincipal? user = null
    )
    {
        DefaultHttpContext context = new() { RequestServices = null! };
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        if (remoteIp is not null)
            context.Connection.RemoteIpAddress = remoteIp;
        if (localPort is not null)
            context.Connection.LocalPort = localPort.Value;
        if (user is not null)
            context.User = user;
        return context;
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using StreamReader reader = new(context.Response.Body);
        return await reader.ReadToEndAsync();
    }

    // =========================================================================
    // Loopback self-ingest bypass — gated on port, not just source IP
    // =========================================================================

    [Fact]
    public async Task LoopbackIngestPort_ValidIngestKey_BypassesAuthAndCallsNext()
    {
        Mock<ILiveIngestKeyStore> ingestKeyStore = new();
        ingestKeyStore.Setup(s => s.TryValidate("valid-key", "/some/file.m3u8")).Returns(true);
        TokenParamAuthMiddleware middleware = CreateMiddleware(
            ingestKeyStore,
            out StrongBox<bool> nextCalled
        );

        DefaultHttpContext context = MakeContext(
            "/some/file.m3u8",
            remoteIp: IPAddress.Loopback,
            localPort: RuntimeServerSettings.Current.InternalServerPort + 1
        );
        context.Request.Headers["X-NoMercy-Ingest-Key"] = "valid-key";

        await middleware.InvokeAsync(context);

        nextCalled.Value.Should().BeTrue();
        // Bypass path never touches the Authorization header.
        context.Request.Headers.Authorization.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task LoopbackIngestPort_InvalidIngestKey_FallsThroughToNormalAuthFlow()
    {
        Mock<ILiveIngestKeyStore> ingestKeyStore = new();
        ingestKeyStore
            .Setup(s => s.TryValidate(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);
        TokenParamAuthMiddleware middleware = CreateMiddleware(
            ingestKeyStore,
            out StrongBox<bool> nextCalled
        );

        DefaultHttpContext context = MakeContext(
            "/random-non-folder-path/file.m3u8",
            remoteIp: IPAddress.Loopback,
            localPort: RuntimeServerSettings.Current.InternalServerPort + 1
        );
        context.Request.Headers["X-NoMercy-Ingest-Key"] = "wrong-key";

        await middleware.InvokeAsync(context);

        // Falls through to the normal flow — since the path isn't under any
        // known folder id, that flow itself calls next() unconditionally.
        nextCalled.Value.Should().BeTrue();
    }

    [Fact]
    public async Task NonLoopbackSource_OnIngestPort_NeverBypasses()
    {
        // Regression guard: a local relay (e.g. cloudflared) forwarding external
        // traffic to 127.0.0.1 must never land here in the first place — but
        // even if something reaches this port from a non-loopback address, the
        // bypass must not fire.
        Mock<ILiveIngestKeyStore> ingestKeyStore = new();
        ingestKeyStore
            .Setup(s => s.TryValidate(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);
        TokenParamAuthMiddleware middleware = CreateMiddleware(
            ingestKeyStore,
            out StrongBox<bool> nextCalled
        );

        DefaultHttpContext context = MakeContext(
            "/random-non-folder-path/file.m3u8",
            remoteIp: IPAddress.Parse("203.0.113.5"),
            localPort: RuntimeServerSettings.Current.InternalServerPort + 1
        );
        context.Request.Headers["X-NoMercy-Ingest-Key"] = "valid-key";

        await middleware.InvokeAsync(context);

        // TryValidate must never even be consulted for a non-loopback source.
        ingestKeyStore.Verify(
            s => s.TryValidate(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never
        );
        nextCalled.Value.Should().BeTrue();
    }

    // =========================================================================
    // Query-param → Authorization header promotion
    // =========================================================================

    [Fact]
    public async Task AccessTokenQueryParam_PromotedToBearerAuthorizationHeader()
    {
        Mock<ILiveIngestKeyStore> ingestKeyStore = new();
        TokenParamAuthMiddleware middleware = CreateMiddleware(
            ingestKeyStore,
            out StrongBox<bool> nextCalled
        );

        DefaultHttpContext context = MakeContext("/random-non-folder-path");
        context.Request.QueryString = new("?access_token=my-jwt-value");

        await middleware.InvokeAsync(context);

        context.Request.Headers.Authorization.ToString().Should().Be("Bearer my-jwt-value");
        nextCalled.Value.Should().BeTrue();
    }

    [Fact]
    public async Task ExistingBearerHeader_IsNeverOverwrittenByQueryParam()
    {
        Mock<ILiveIngestKeyStore> ingestKeyStore = new();
        TokenParamAuthMiddleware middleware = CreateMiddleware(ingestKeyStore, out _);

        DefaultHttpContext context = MakeContext("/random-non-folder-path");
        context.Request.Headers.Authorization = "Bearer already-set-token";
        context.Request.QueryString = new("?access_token=should-be-ignored");

        await middleware.InvokeAsync(context);

        context.Request.Headers.Authorization.ToString().Should().Be("Bearer already-set-token");
    }

    [Fact]
    public async Task NoTokenQueryParamAndNoBearer_LeavesAuthorizationHeaderEmpty()
    {
        Mock<ILiveIngestKeyStore> ingestKeyStore = new();
        TokenParamAuthMiddleware middleware = CreateMiddleware(ingestKeyStore, out _);

        DefaultHttpContext context = MakeContext("/random-non-folder-path");

        await middleware.InvokeAsync(context);

        context.Request.Headers.Authorization.ToString().Should().BeEmpty();
    }

    // =========================================================================
    // Non-folder-scoped path: always calls next() regardless of auth state
    // =========================================================================

    [Fact]
    public async Task PathNotUnderAnyFolderId_AlwaysCallsNext_EvenWithoutBearer()
    {
        Mock<ILiveIngestKeyStore> ingestKeyStore = new();
        TokenParamAuthMiddleware middleware = CreateMiddleware(
            ingestKeyStore,
            out StrongBox<bool> nextCalled
        );

        DefaultHttpContext context = MakeContext("/api/v1/media/movies");

        await middleware.InvokeAsync(context);

        nextCalled.Value.Should().BeTrue();
    }

    // =========================================================================
    // Folder-scoped path: 401/403 problem responses, then success
    // =========================================================================

    [Fact]
    public async Task FolderScopedPath_AlreadyHasBearerHeader_CallsNext()
    {
        Mock<ILiveIngestKeyStore> ingestKeyStore = new();
        TokenParamAuthMiddleware middleware = CreateMiddleware(
            ingestKeyStore,
            out StrongBox<bool> nextCalled
        );

        DefaultHttpContext context = MakeContext($"/{NoMercyApiFactory.MovieFolderId}/movie.m3u8");
        context.Request.Headers.Authorization = "Bearer some-jwt";

        await middleware.InvokeAsync(context);

        nextCalled.Value.Should().BeTrue();
    }

    [Fact]
    public async Task FolderScopedPath_NoBearerAndNoClaim_Returns401NoToken()
    {
        Mock<ILiveIngestKeyStore> ingestKeyStore = new();
        TokenParamAuthMiddleware middleware = CreateMiddleware(
            ingestKeyStore,
            out StrongBox<bool> nextCalled
        );

        DefaultHttpContext context = MakeContext(
            $"/{NoMercyApiFactory.MovieFolderId}/movie.m3u8",
            user: new ClaimsPrincipal(new ClaimsIdentity())
        );

        await middleware.InvokeAsync(context);

        nextCalled.Value.Should().BeFalse();
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
        string body = await ReadBodyAsync(context);
        using JsonDocument json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("authError").GetString().Should().Be("NO_TOKEN");
    }

    [Fact]
    public async Task FolderScopedPath_MalformedGuidClaim_Returns403InvalidToken()
    {
        Mock<ILiveIngestKeyStore> ingestKeyStore = new();
        TokenParamAuthMiddleware middleware = CreateMiddleware(ingestKeyStore, out _);

        ClaimsPrincipal principal = new(
            new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "not-a-guid")])
        );
        DefaultHttpContext context = MakeContext(
            $"/{NoMercyApiFactory.MovieFolderId}/movie.m3u8",
            user: principal
        );

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be((int)HttpStatusCode.Forbidden);
        string body = await ReadBodyAsync(context);
        using JsonDocument json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("authError").GetString().Should().Be("INVALID_TOKEN");
    }

    [Fact]
    public async Task FolderScopedPath_UnknownUser_Returns403UserNotFound()
    {
        Mock<ILiveIngestKeyStore> ingestKeyStore = new();
        TokenParamAuthMiddleware middleware = CreateMiddleware(ingestKeyStore, out _);

        ClaimsPrincipal principal = new(
            new ClaimsIdentity([new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())])
        );
        DefaultHttpContext context = MakeContext(
            $"/{NoMercyApiFactory.MovieFolderId}/movie.m3u8",
            user: principal
        );

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be((int)HttpStatusCode.Forbidden);
        string body = await ReadBodyAsync(context);
        using JsonDocument json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("authError").GetString().Should().Be("USER_NOT_FOUND");
    }

    [Fact]
    public async Task FolderScopedPath_KnownSeededUser_CallsNext()
    {
        Mock<ILiveIngestKeyStore> ingestKeyStore = new();
        TokenParamAuthMiddleware middleware = CreateMiddleware(
            ingestKeyStore,
            out StrongBox<bool> nextCalled
        );

        ClaimsPrincipal principal = new(
            new ClaimsIdentity([
                new(ClaimTypes.NameIdentifier, TestAuthHandler.DefaultUserId.ToString()),
            ])
        );
        DefaultHttpContext context = MakeContext(
            $"/{NoMercyApiFactory.MovieFolderId}/movie.m3u8",
            user: principal
        );

        await middleware.InvokeAsync(context);

        nextCalled.Value.Should().BeTrue();
    }
}
