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

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using NoMercy.Api.Middleware;
using NoMercy.Database;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Security;
using NoMercy.Setup.Auth;
using NoMercy.Setup.Server;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Tests.Setup.Infrastructure;

namespace NoMercy.Tests.Setup;

public class SetupModeMiddlewareTests
{
    private sealed class NoOpHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static SetupEndpoints CreateSetupEndpoints(SetupState state)
    {
        ServiceCollection services = new();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        ServiceProvider provider = services.BuildServiceProvider();
        TokenStore.Initialize(provider);

        DbContextOptionsBuilder<AppDbContext> optionsBuilder = new();
        optionsBuilder.UseSqlite("Data Source=:memory:");
        AppDbContext dbContext = new(optionsBuilder.Options);
        dbContext.Database.OpenConnection();
        dbContext.Database.EnsureCreated();

        AuthManager authManager = new(dbContext, new LocalStorageDriver(), new AuthTokenStore());
        return new(
            state,
            authManager,
            new FakeServerRegistrationService(),
            new NoOpHttpClientFactory(),
            Mock.Of<IHostApplicationLifetime>()
        );
    }

    private static SetupModeMiddleware CreateMiddleware(
        SetupState state,
        RequestDelegate? next = null
    )
    {
        next ??= _ => Task.CompletedTask;
        SetupEndpoints setupEndpoints = CreateSetupEndpoints(state);
        return new(next, state, setupEndpoints);
    }

    private static DefaultHttpContext CreateContext(string path)
    {
        DefaultHttpContext context = new()
        {
            Request = { Path = path },
            Response = { Body = new MemoryStream() },
        };
        return context;
    }

    private static async Task<string> ReadResponseBody(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using StreamReader reader = new(context.Response.Body);
        return await reader.ReadToEndAsync();
    }

    // --- Setup required: non-setup routes return 503 ---

    [Fact]
    public async Task NonSetupRoute_WhenSetupRequired_Returns503()
    {
        SetupState state = new();
        SetupModeMiddleware middleware = CreateMiddleware(state);
        DefaultHttpContext context = CreateContext("/api/v1/libraries");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    [Fact]
    public async Task NonSetupRoute_WhenSetupRequired_ReturnsJsonBody()
    {
        SetupState state = new();
        SetupModeMiddleware middleware = CreateMiddleware(state);
        DefaultHttpContext context = CreateContext("/api/v1/libraries");

        await middleware.InvokeAsync(context);

        string body = await ReadResponseBody(context);
        dynamic? data = JsonConvert.DeserializeObject<dynamic>(body);

        Assert.NotNull(data);
        Assert.Equal("setup_required", (string)data!.status);
        Assert.Equal("Server is in setup mode", (string)data.message);
        Assert.Equal("/setup", (string)data.setup_url);
    }

    [Fact]
    public async Task NonSetupRoute_WhenSetupRequired_SetsJsonContentType()
    {
        SetupState state = new();
        SetupModeMiddleware middleware = CreateMiddleware(state);
        DefaultHttpContext context = CreateContext("/api/v1/movies");

        await middleware.InvokeAsync(context);

        Assert.Equal("application/json", context.Response.ContentType);
    }

    [Fact]
    public async Task RootRoute_WhenSetupRequired_Returns503()
    {
        SetupState state = new();
        SetupModeMiddleware middleware = CreateMiddleware(state);
        DefaultHttpContext context = CreateContext("/");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    [Fact]
    public async Task RandomPath_WhenSetupRequired_Returns503()
    {
        SetupState state = new();
        SetupModeMiddleware middleware = CreateMiddleware(state);
        DefaultHttpContext context = CreateContext("/some/random/path");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    [Fact]
    public async Task NonSetupRoute_WhenSetupRequired_DoesNotCallNext()
    {
        SetupState state = new();
        bool nextCalled = false;
        SetupModeMiddleware middleware = CreateMiddleware(
            state,
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            }
        );
        DefaultHttpContext context = CreateContext("/api/v1/libraries");

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
    }

    // --- Setup required: setup routes pass through ---

    [Fact]
    public async Task SetupRoute_WhenSetupRequired_IsHandledDirectly()
    {
        SetupState state = new();
        bool nextCalled = false;
        SetupModeMiddleware middleware = CreateMiddleware(
            state,
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            }
        );
        DefaultHttpContext context = CreateContext("/setup");

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.NotEqual(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    [Fact]
    public async Task SetupStatusRoute_WhenSetupRequired_IsHandledDirectly()
    {
        SetupState state = new();
        bool nextCalled = false;
        SetupModeMiddleware middleware = CreateMiddleware(
            state,
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            }
        );
        DefaultHttpContext context = CreateContext("/setup/status");

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.NotEqual(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    [Fact]
    public async Task SetupConfigRoute_WhenSetupRequired_IsHandledDirectly()
    {
        SetupState state = new();
        bool nextCalled = false;
        SetupModeMiddleware middleware = CreateMiddleware(
            state,
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            }
        );
        DefaultHttpContext context = CreateContext("/setup/config");

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.NotEqual(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    [Fact]
    public async Task SsoCallbackRoute_WhenSetupRequired_IsHandledDirectly()
    {
        SetupState state = new();
        bool nextCalled = false;
        SetupModeMiddleware middleware = CreateMiddleware(
            state,
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            }
        );
        DefaultHttpContext context = CreateContext("/sso-callback");

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.NotEqual(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    [Fact]
    public async Task HealthRoute_WhenSetupRequired_CallsNext()
    {
        SetupState state = new();
        bool nextCalled = false;
        SetupModeMiddleware middleware = CreateMiddleware(
            state,
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            }
        );
        DefaultHttpContext context = CreateContext("/health");

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    // --- Setup complete: all routes pass through ---

    [Fact]
    public async Task AnyRoute_WhenSetupComplete_CallsNext()
    {
        SetupState state = new();
        state.DetermineInitialPhase(hasValidToken: true, isRegistered: true);

        bool nextCalled = false;
        SetupModeMiddleware middleware = CreateMiddleware(
            state,
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            }
        );
        DefaultHttpContext context = CreateContext("/api/v1/libraries");

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task AnyRoute_WhenSetupComplete_DoesNotReturn503()
    {
        SetupState state = new();
        state.DetermineInitialPhase(hasValidToken: true, isRegistered: true);

        SetupModeMiddleware middleware = CreateMiddleware(state);
        DefaultHttpContext context = CreateContext("/api/v1/libraries");

        await middleware.InvokeAsync(context);

        Assert.NotEqual(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    // --- Trailing slash handling ---

    [Fact]
    public async Task SetupRouteWithTrailingSlash_WhenSetupRequired_IsHandledDirectly()
    {
        SetupState state = new();
        bool nextCalled = false;
        SetupModeMiddleware middleware = CreateMiddleware(
            state,
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            }
        );
        DefaultHttpContext context = CreateContext("/setup/");

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.NotEqual(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    // --- Case insensitivity ---

    [Fact]
    public async Task SetupRouteUpperCase_WhenSetupRequired_IsHandledDirectly()
    {
        SetupState state = new();
        bool nextCalled = false;
        SetupModeMiddleware middleware = CreateMiddleware(
            state,
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            }
        );
        DefaultHttpContext context = CreateContext("/Setup");

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.NotEqual(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    // --- SignalR hubs blocked during setup ---

    [Fact]
    public async Task SignalRHub_WhenSetupRequired_Returns503()
    {
        SetupState state = new();
        SetupModeMiddleware middleware = CreateMiddleware(state);
        DefaultHttpContext context = CreateContext("/videoHub");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    [Fact]
    public async Task SwaggerRoute_WhenSetupRequired_Returns503()
    {
        SetupState state = new();
        SetupModeMiddleware middleware = CreateMiddleware(state);
        DefaultHttpContext context = CreateContext("/swagger");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }
}
