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
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Api.Middleware;
using NoMercy.Authorization;
using NoMercy.Database;
using NoMercy.Database.Models.Users;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Middleware;

/// <summary>
/// Coverage for AccessLogMiddleware's gating: ignored route prefixes/exact
/// routes/folder paths skip both auth AND logging, the guest/malformed-guid
/// 401 problem responses (with the single "/status" ignoreIfGuest exception),
/// and the cache-miss-triggers-RefreshUsersAsync retry path. Uses
/// NoMercyApiFactory for its seeded UserCache/MediaContext DI wiring.
/// </summary>
[Trait("Category", "Unit")]
public class AccessLogMiddlewareTests : IClassFixture<NoMercyApiFactory>
{
    private readonly NoMercyApiFactory _factory;

    public AccessLogMiddlewareTests(NoMercyApiFactory factory)
    {
        _factory = factory;
        _factory.CreateClient();
    }

    private static AccessLogMiddleware CreateMiddleware(out StrongBox<bool> nextCalled)
    {
        StrongBox<bool> called = new(false);
        AccessLogMiddleware middleware = new(
            _ =>
            {
                called.Value = true;
                return Task.CompletedTask;
            },
            NullLogger<AccessLogMiddleware>.Instance,
            UserCache.Current
        );
        nextCalled = called;
        return middleware;
    }

    // A matched endpoint with no [AllowAnonymous] metadata — the shape a real
    // [Authorize]-protected controller action carries once UseRouting has run.
    // These tests invoke AccessLogMiddleware directly (no real routing pass), so
    // without this the middleware's new endpoint-metadata check would see a null
    // endpoint and treat every synthetic context below as anonymous-exempt,
    // masking the guid/user checks these tests exist to cover. Tests for the
    // actual [AllowAnonymous] gate live in AnonymousRouteAccessTests, which runs
    // the real pipeline end to end.
    private static readonly Endpoint ProtectedEndpoint = new(
        _ => Task.CompletedTask,
        new EndpointMetadataCollection(),
        "test-protected-endpoint"
    );

    private DefaultHttpContext MakeContext(
        string path,
        ClaimsPrincipal? user = null,
        bool withRequestServices = false
    )
    {
        DefaultHttpContext context = new() { RequestServices = null! };
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(ProtectedEndpoint);
        if (user is not null)
            context.User = user;
        if (withRequestServices)
            context.RequestServices = _factory.Services.CreateScope().ServiceProvider;
        return context;
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using StreamReader reader = new(context.Response.Body);
        return await reader.ReadToEndAsync();
    }

    // =========================================================================
    // Ignored routes: never touch auth, always call next()
    // =========================================================================

    [Theory]
    [InlineData("/images/poster.jpg")]
    [InlineData("/swagger/index.html")]
    [InlineData("/videoHub/negotiate")]
    [InlineData("/dashboardHub")]
    public async Task IgnoredStartsWithRoute_CallsNext_NoAuthRequired(string path)
    {
        AccessLogMiddleware middleware = CreateMiddleware(out StrongBox<bool> nextCalled);
        DefaultHttpContext context = MakeContext(path);

        await middleware.InvokeAsync(context);

        nextCalled.Value.Should().BeTrue();
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/api/v1/dashboard/logs")]
    [InlineData("/api/v1/setup/server-info")]
    public async Task IgnoredExactRoute_CallsNext_NoAuthRequired(string path)
    {
        AccessLogMiddleware middleware = CreateMiddleware(out StrongBox<bool> nextCalled);
        DefaultHttpContext context = MakeContext(path);

        await middleware.InvokeAsync(context);

        nextCalled.Value.Should().BeTrue();
    }

    [Fact]
    public async Task FolderScopedPath_CallsNext_NoAuthRequired()
    {
        AccessLogMiddleware middleware = CreateMiddleware(out StrongBox<bool> nextCalled);
        DefaultHttpContext context = MakeContext($"/{NoMercyApiFactory.MovieFolderId}/movie.m3u8");

        await middleware.InvokeAsync(context);

        nextCalled.Value.Should().BeTrue();
    }

    // =========================================================================
    // No / malformed GUID claim
    // =========================================================================

    [Fact]
    public async Task NoClaim_NonGuestRoute_Returns401NoToken()
    {
        AccessLogMiddleware middleware = CreateMiddleware(out StrongBox<bool> nextCalled);
        DefaultHttpContext context = MakeContext(
            "/api/v1/media/movies",
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
    public async Task NoClaim_StatusRoute_IsIgnoreIfGuest_CallsNext()
    {
        AccessLogMiddleware middleware = CreateMiddleware(out StrongBox<bool> nextCalled);
        DefaultHttpContext context = MakeContext(
            "/status",
            user: new ClaimsPrincipal(new ClaimsIdentity())
        );

        await middleware.InvokeAsync(context);

        nextCalled.Value.Should().BeTrue();
    }

    [Fact]
    public async Task MalformedGuidClaim_NonGuestRoute_Returns401InvalidToken()
    {
        AccessLogMiddleware middleware = CreateMiddleware(out StrongBox<bool> nextCalled);
        ClaimsPrincipal principal = new(
            new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "not-a-guid")])
        );
        DefaultHttpContext context = MakeContext("/api/v1/media/movies", user: principal);

        await middleware.InvokeAsync(context);

        nextCalled.Value.Should().BeFalse();
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
        string body = await ReadBodyAsync(context);
        using JsonDocument json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("authError").GetString().Should().Be("INVALID_TOKEN");
    }

    [Fact]
    public async Task MalformedGuidClaim_StatusRoute_IsIgnoreIfGuest_CallsNext()
    {
        AccessLogMiddleware middleware = CreateMiddleware(out StrongBox<bool> nextCalled);
        ClaimsPrincipal principal = new(
            new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "not-a-guid")])
        );
        DefaultHttpContext context = MakeContext("/status", user: principal);

        await middleware.InvokeAsync(context);

        nextCalled.Value.Should().BeTrue();
    }

    [Fact]
    public async Task EmptyGuidClaim_NonGuestRoute_Returns401InvalidToken()
    {
        AccessLogMiddleware middleware = CreateMiddleware(out StrongBox<bool> nextCalled);
        ClaimsPrincipal principal = new(
            new ClaimsIdentity([new(ClaimTypes.NameIdentifier, Guid.Empty.ToString())])
        );
        DefaultHttpContext context = MakeContext("/api/v1/media/movies", user: principal);

        await middleware.InvokeAsync(context);

        nextCalled.Value.Should().BeFalse();
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
    }

    // =========================================================================
    // Valid GUID: known user vs unknown user (with the cache-miss refresh retry)
    // =========================================================================

    [Fact]
    public async Task KnownSeededUser_CallsNext()
    {
        AccessLogMiddleware middleware = CreateMiddleware(out StrongBox<bool> nextCalled);
        ClaimsPrincipal principal = new(
            new ClaimsIdentity([
                new(ClaimTypes.NameIdentifier, TestAuthHandler.DefaultUserId.ToString()),
            ])
        );
        DefaultHttpContext context = MakeContext(
            "/api/v1/media/movies",
            user: principal,
            withRequestServices: true
        );

        await middleware.InvokeAsync(context);

        nextCalled.Value.Should().BeTrue();
    }

    [Fact]
    public async Task UserNotInCache_ButExistsInDatabase_RefreshRetrySucceeds_CallsNext()
    {
        // Regression coverage for the cache-miss retry: insert a user directly
        // into the database WITHOUT adding it to UserCache first — simulating
        // the real race the comment on line ~174 describes (cache not yet
        // populated at startup). The middleware's RefreshUsersAsync fallback
        // must find it and let the request through.
        IDbContextFactory<MediaContext> contextFactory = _factory.Services.GetRequiredService<
            IDbContextFactory<MediaContext>
        >();
        await using MediaContext ctx = await contextFactory.CreateDbContextAsync();
        User user = new()
        {
            Id = Guid.NewGuid(),
            Email = $"{Guid.NewGuid()}@nomercy.tv",
            Name = "Cache Miss Test User",
            Owner = false,
            Allowed = true,
            Manage = false,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();

        try
        {
            AccessLogMiddleware middleware = CreateMiddleware(out StrongBox<bool> nextCalled);
            ClaimsPrincipal principal = new(
                new ClaimsIdentity([new(ClaimTypes.NameIdentifier, user.Id.ToString())])
            );
            DefaultHttpContext context = MakeContext(
                "/api/v1/media/movies",
                user: principal,
                withRequestServices: true
            );

            await middleware.InvokeAsync(context);

            nextCalled.Value.Should().BeTrue();
        }
        finally
        {
            // Leave the DB row (harmless, isolated by fresh Guid) but keep the
            // process-wide UserCache from accumulating test users indefinitely.
            User? cached = UserCache.Current.GetUser(user.Id);
            if (cached is not null)
                UserCache.Current.RemoveUser(cached);
        }
    }

    [Fact]
    public async Task UnknownUser_NotInCacheOrDatabase_Returns401UserNotFound()
    {
        AccessLogMiddleware middleware = CreateMiddleware(out StrongBox<bool> nextCalled);
        ClaimsPrincipal principal = new(
            new ClaimsIdentity([new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())])
        );
        DefaultHttpContext context = MakeContext(
            "/api/v1/media/movies",
            user: principal,
            withRequestServices: true
        );

        await middleware.InvokeAsync(context);

        nextCalled.Value.Should().BeFalse();
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
        string body = await ReadBodyAsync(context);
        using JsonDocument json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("authError").GetString().Should().Be("USER_NOT_FOUND");
    }
}
