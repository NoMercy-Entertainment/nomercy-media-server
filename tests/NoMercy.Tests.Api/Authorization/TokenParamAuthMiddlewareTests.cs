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
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Api.Middleware;
using NoMercy.Api.Services;
using NoMercy.Authorization;
using NoMercy.Authorization.LiveIngest;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Storage;
using NoMercy.Database.Models.Users;
using NoMercy.NmSystem.Configuration;
using Xunit;

// ReSharper disable AccessToDisposedClosure

namespace NoMercy.Tests.Api.Authorization;

[Trait("Category", "Authorization")]
public sealed class TokenParamAuthMiddlewareTests : IAsyncLifetime, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _dbOptions;

    private static readonly Ulid KnownFolderId = Ulid.NewUlid();
    private static readonly Guid KnownUserId = Guid.NewGuid();

    public TokenParamAuthMiddlewareTests()
    {
        _connection = new($"DataSource={Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<MediaContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new SqliteNormalizeSearchInterceptor())
            .Options;

        using MediaContext ctx = new(_dbOptions);
        ctx.Database.EnsureCreated();

        Driver driver = new()
        {
            Id = Driver.SystemLocalDriverId,
            Name = "Local",
            Type = "local",
            Config = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        ctx.Drivers.Add(driver);

        Folder folder = new()
        {
            Id = KnownFolderId,
            Path = "/media",
            DriverId = Driver.SystemLocalDriverId,
        };
        ctx.Folders.Add(folder);

        User user = new()
        {
            Id = KnownUserId,
            Name = "Known",
            Email = "k@nm.tv",
            Allowed = true,
        };
        ctx.Users.Add(user);
        ctx.SaveChanges();
    }

    public async Task InitializeAsync()
    {
        UserCache.Current.Reset();

        await using MediaContext ctx = new(_dbOptions);
        await UserCache.Current.InitializeAsync(ctx);
    }

    public Task DisposeAsync()
    {
        UserCache.Current.Reset();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    private static TokenParamAuthMiddleware BuildMiddleware(
        RequestDelegate next,
        ILiveIngestKeyStore? ingestKeyStore = null
    )
    {
        return new(
            next,
            ingestKeyStore ?? new LiveIngestKeyStore(),
            NullLogger<TokenParamAuthMiddleware>.Instance,
            UserCache.Current
        );
    }

    private static readonly int IngestPort = RuntimeServerSettings.Current.InternalServerPort + 1;

    private static HttpContext BuildContext(
        string path,
        string? authorizationHeader = null,
        ClaimsPrincipal? user = null,
        string? ingestKey = null,
        IPAddress? remoteIp = null,
        int? localPort = null
    )
    {
        DefaultHttpContext context = new();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        if (authorizationHeader is not null)
            context.Request.Headers.Authorization = authorizationHeader;

        if (ingestKey is not null)
            context.Request.Headers["X-NoMercy-Ingest-Key"] = ingestKey;

        if (remoteIp is not null)
            context.Connection.RemoteIpAddress = remoteIp;

        if (localPort is not null)
            context.Connection.LocalPort = localPort.Value;

        if (user is not null)
            context.User = user;

        return context;
    }

    private static ClaimsPrincipal PrincipalWithSub(string sub)
    {
        List<Claim> claims = [new(ClaimTypes.NameIdentifier, sub)];
        return new(new ClaimsIdentity(claims, "TestScheme"));
    }

    [Fact]
    public async Task Allows_WhenUrlIsNotAFolderPath()
    {
        bool nextCalled = false;
        TokenParamAuthMiddleware middleware = BuildMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        HttpContext context = BuildContext("/api/v1/media/movies");

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Allows_WhenFolderPathAndBearerTokenPresent()
    {
        bool nextCalled = false;
        TokenParamAuthMiddleware middleware = BuildMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        HttpContext context = BuildContext(
            $"/{KnownFolderId}/some-file.mkv",
            authorizationHeader: "Bearer eyJhbGciOiJSUzI1NiJ9.test.sig"
        );

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Denies_WithUnauthorized_WhenFolderPathAndNoAuth()
    {
        bool nextCalled = false;
        TokenParamAuthMiddleware middleware = BuildMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        HttpContext context = BuildContext($"/{KnownFolderId}/some-file.mkv");

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task Denies_WithForbidden_WhenFolderPathAndSubMalformed()
    {
        bool nextCalled = false;
        TokenParamAuthMiddleware middleware = BuildMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        HttpContext context = BuildContext(
            $"/{KnownFolderId}/some-file.mkv",
            user: PrincipalWithSub("not-a-guid")
        );

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task Denies_WithForbidden_WhenFolderPathAndUserNotInCache()
    {
        bool nextCalled = false;
        TokenParamAuthMiddleware middleware = BuildMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        Guid unknownId = Guid.NewGuid();
        HttpContext context = BuildContext(
            $"/{KnownFolderId}/some-file.mkv",
            user: PrincipalWithSub(unknownId.ToString())
        );

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task Allows_WhenFolderPathAndKnownUserInCacheWithNoBearer()
    {
        bool nextCalled = false;
        TokenParamAuthMiddleware middleware = BuildMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        HttpContext context = BuildContext(
            $"/{KnownFolderId}/some-file.mkv",
            user: PrincipalWithSub(KnownUserId.ToString())
        );

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Allows_WhenValidIngestKeyOnIngestPortFromLoopback_NoBearerNoUser()
    {
        LiveIngestKeyStore store = new();
        string path = $"/{KnownFolderId}/some-file.mkv";
        string key = store.Issue(path);

        bool nextCalled = false;
        TokenParamAuthMiddleware middleware = BuildMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            store
        );
        HttpContext context = BuildContext(
            path,
            ingestKey: key,
            remoteIp: IPAddress.Loopback,
            localPort: IngestPort
        );

        await middleware.InvokeAsync(context);

        // A loopback ffmpeg self-ingest on the internal ingest port carries only the
        // scoped key, no bearer and no authenticated user — it must still be served.
        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Denies_WhenValidIngestKeyArrivesOnPublicPort()
    {
        LiveIngestKeyStore store = new();
        string path = $"/{KnownFolderId}/some-file.mkv";
        string key = store.Issue(path);

        bool nextCalled = false;
        TokenParamAuthMiddleware middleware = BuildMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            store
        );
        // The cloudflared-relay case: a valid key from a loopback source IP, but the
        // request landed on the PUBLIC port (relayed external traffic), not the
        // loopback-only ingest port. The bypass must not fire — it falls through to
        // the normal 401 (folder path, no auth).
        HttpContext context = BuildContext(
            path,
            ingestKey: key,
            remoteIp: IPAddress.Loopback,
            localPort: RuntimeServerSettings.Current.InternalServerPort
        );

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task Denies_WhenValidIngestKeyButNotFromLoopback()
    {
        LiveIngestKeyStore store = new();
        string path = $"/{KnownFolderId}/some-file.mkv";
        string key = store.Issue(path);

        bool nextCalled = false;
        TokenParamAuthMiddleware middleware = BuildMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            store
        );
        // Same valid key, correct port, but a public source address — denied.
        HttpContext context = BuildContext(
            path,
            ingestKey: key,
            remoteIp: IPAddress.Parse("203.0.113.7"),
            localPort: IngestPort
        );

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task Denies_WhenIngestKeyBoundToDifferentFolder()
    {
        LiveIngestKeyStore store = new();
        string key = store.Issue($"/{KnownFolderId}/ShowA/Ep/Ep.NoMercy.m3u8");

        bool nextCalled = false;
        TokenParamAuthMiddleware middleware = BuildMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            store
        );
        // Valid key, loopback, ingest port, but a different title's folder than it
        // was minted for — the key authorizes its own folder subtree, not a sibling.
        HttpContext context = BuildContext(
            $"/{KnownFolderId}/ShowB/Ep/Ep.NoMercy.m3u8",
            ingestKey: key,
            remoteIp: IPAddress.Loopback,
            localPort: IngestPort
        );

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task Allows_NestedResourceUnderIngestKeyFolder()
    {
        LiveIngestKeyStore store = new();
        // Minted for the encoded master; ffmpeg then self-ingests the nested video
        // variant playlist under the same folder — the case that broke on .60.
        string key = store.Issue($"/{KnownFolderId}/Show/Ep/Ep.NoMercy.m3u8");

        bool nextCalled = false;
        TokenParamAuthMiddleware middleware = BuildMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            store
        );
        HttpContext context = BuildContext(
            $"/{KnownFolderId}/Show/Ep/video_1920x1080_SDR/video_1920x1080_SDR.m3u8",
            ingestKey: key,
            remoteIp: IPAddress.Loopback,
            localPort: IngestPort
        );

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
    }
}
