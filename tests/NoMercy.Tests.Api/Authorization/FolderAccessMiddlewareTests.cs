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
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Api.Middleware;
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

/// <summary>
/// FolderAccessMiddleware is the only gate in front of library file serving. A
/// folder path passes when the request is a loopback self-ingest for that exact
/// file, or when the caller is an authenticated, allowed user with a library grant
/// covering the folder. A bearer header alone proves nothing: the JWT handler
/// leaves the principal anonymous on a bad token and only logs.
/// </summary>
[Trait("Category", "Authorization")]
public sealed class FolderAccessMiddlewareTests : IAsyncLifetime, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _dbOptions;

    private static readonly Ulid GrantedFolderId = Ulid.NewUlid();
    private static readonly Ulid OtherFolderId = Ulid.NewUlid();
    private static readonly Ulid GrantedLibraryId = Ulid.NewUlid();
    private static readonly Ulid OtherLibraryId = Ulid.NewUlid();
    private static readonly Guid GrantedUserId = Guid.NewGuid();
    private static readonly Guid NotAllowedUserId = Guid.NewGuid();

    public FolderAccessMiddlewareTests()
    {
        _connection = new($"DataSource={Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<MediaContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new SqliteNormalizeSearchInterceptor())
            .Options;

        using MediaContext ctx = new(_dbOptions);
        ctx.Database.EnsureCreated();

        ctx.Drivers.Add(
            new Driver
            {
                Id = Driver.SystemLocalDriverId,
                Name = "Local",
                Type = "local",
                Config = "{}",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            }
        );
        ctx.Folders.AddRange(
            new Folder
            {
                Id = GrantedFolderId,
                Path = "/media/granted",
                DriverId = Driver.SystemLocalDriverId,
            },
            new Folder
            {
                Id = OtherFolderId,
                Path = "/media/other",
                DriverId = Driver.SystemLocalDriverId,
            }
        );
        ctx.Libraries.AddRange(
            new Library
            {
                Id = GrantedLibraryId,
                Title = "Granted",
                Type = "movie",
                Order = 1,
            },
            new Library
            {
                Id = OtherLibraryId,
                Title = "Other",
                Type = "movie",
                Order = 2,
            }
        );
        ctx.Users.AddRange(
            new User
            {
                Id = GrantedUserId,
                Name = "Granted",
                Email = "granted@nm.tv",
                Allowed = true,
            },
            new User
            {
                Id = NotAllowedUserId,
                Name = "Pending",
                Email = "pending@nm.tv",
                Allowed = false,
            }
        );
        ctx.SaveChanges();

        ctx.FolderLibrary.AddRange(
            new FolderLibrary(GrantedFolderId, GrantedLibraryId),
            new FolderLibrary(OtherFolderId, OtherLibraryId)
        );
        ctx.LibraryUser.AddRange(
            new LibraryUser(GrantedLibraryId, GrantedUserId),
            new LibraryUser(GrantedLibraryId, NotAllowedUserId)
        );
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

    private static readonly int IngestPort = RuntimeServerSettings.Current.InternalServerPort + 1;

    private static FolderAccessMiddleware BuildMiddleware(
        RequestDelegate next,
        ILiveIngestKeyStore? ingestKeyStore = null
    ) =>
        new(
            next,
            UserCache.Current,
            ingestKeyStore ?? new LiveIngestKeyStore(),
            NullLogger<FolderAccessMiddleware>.Instance
        );

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

    private static ClaimsPrincipal AuthenticatedWithSub(string sub)
    {
        List<Claim> claims = [new(ClaimTypes.NameIdentifier, sub)];
        return new(new ClaimsIdentity(claims, "TestScheme"));
    }

    private static async Task<string> AuthErrorOf(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using StreamReader reader = new(context.Response.Body);
        using JsonDocument json = JsonDocument.Parse(await reader.ReadToEndAsync());
        return json.RootElement.GetProperty("authError").GetString()!;
    }

    private static (FolderAccessMiddleware Middleware, Func<bool> NextCalled) Build(
        ILiveIngestKeyStore? store = null
    )
    {
        bool called = false;
        FolderAccessMiddleware middleware = BuildMiddleware(
            _ =>
            {
                called = true;
                return Task.CompletedTask;
            },
            store
        );
        return (middleware, () => called);
    }

    // =========================================================================
    // Not a folder path: never gated here
    // =========================================================================

    [Fact]
    public async Task Allows_WhenUrlIsNotAFolderPath()
    {
        (FolderAccessMiddleware middleware, Func<bool> nextCalled) = Build();

        await middleware.InvokeAsync(BuildContext("/api/v1/media/movies"));

        nextCalled().Should().BeTrue();
    }

    [Fact]
    public async Task Allows_WhenPathLooksLikeUlidButNoSuchFolderIsRegistered()
    {
        (FolderAccessMiddleware middleware, Func<bool> nextCalled) = Build();

        await middleware.InvokeAsync(BuildContext($"/{Ulid.NewUlid()}/some-file.mkv"));

        // Nothing to protect: DynamicStaticFilesMiddleware has no folder to serve
        // and the request falls through to a 404 downstream.
        nextCalled().Should().BeTrue();
    }

    // =========================================================================
    // Folder path: the regression the researcher found, and the full gate
    // =========================================================================

    [Fact]
    public async Task Denies_WithUnauthorized_WhenFolderPathAndNoAuth()
    {
        (FolderAccessMiddleware middleware, Func<bool> nextCalled) = Build();
        HttpContext context = BuildContext($"/{GrantedFolderId}/some-file.mkv");

        await middleware.InvokeAsync(context);

        nextCalled().Should().BeFalse();
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
        (await AuthErrorOf(context)).Should().Be("NO_TOKEN");
    }

    [Fact]
    public async Task Denies_WithUnauthorized_WhenBearerHeaderPresentButPrincipalIsAnonymous()
    {
        // A garbage, expired or forged token: the JWT handler rejects it, logs,
        // and leaves the principal anonymous. The word "Bearer" in the header
        // must not be mistaken for authentication.
        (FolderAccessMiddleware middleware, Func<bool> nextCalled) = Build();
        HttpContext context = BuildContext(
            $"/{GrantedFolderId}/some-file.mkv",
            authorizationHeader: "Bearer this.is.garbage",
            user: new ClaimsPrincipal(new ClaimsIdentity())
        );

        await middleware.InvokeAsync(context);

        nextCalled().Should().BeFalse();
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
        (await AuthErrorOf(context)).Should().Be("NO_TOKEN");
    }

    [Fact]
    public async Task Denies_WithForbidden_WhenSubMalformed()
    {
        (FolderAccessMiddleware middleware, Func<bool> nextCalled) = Build();
        HttpContext context = BuildContext(
            $"/{GrantedFolderId}/some-file.mkv",
            user: AuthenticatedWithSub("not-a-guid")
        );

        await middleware.InvokeAsync(context);

        nextCalled().Should().BeFalse();
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.Forbidden);
        (await AuthErrorOf(context)).Should().Be("INVALID_TOKEN");
    }

    [Fact]
    public async Task Denies_WithForbidden_WhenUserNotRegisteredOnThisServer()
    {
        (FolderAccessMiddleware middleware, Func<bool> nextCalled) = Build();
        HttpContext context = BuildContext(
            $"/{GrantedFolderId}/some-file.mkv",
            user: AuthenticatedWithSub(Guid.NewGuid().ToString())
        );

        await middleware.InvokeAsync(context);

        nextCalled().Should().BeFalse();
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.Forbidden);
        (await AuthErrorOf(context)).Should().Be("USER_NOT_FOUND");
    }

    [Fact]
    public async Task Denies_WithForbidden_WhenUserIsRegisteredButNotAllowed()
    {
        // Same rule the MediaAccess policy applies to every API endpoint.
        (FolderAccessMiddleware middleware, Func<bool> nextCalled) = Build();
        HttpContext context = BuildContext(
            $"/{GrantedFolderId}/some-file.mkv",
            user: AuthenticatedWithSub(NotAllowedUserId.ToString())
        );

        await middleware.InvokeAsync(context);

        nextCalled().Should().BeFalse();
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.Forbidden);
        (await AuthErrorOf(context)).Should().Be("NOT_ALLOWED");
    }

    [Fact]
    public async Task Denies_WithForbidden_WhenFolderBelongsToALibraryTheUserWasNotGranted()
    {
        // The per-library restriction every browse repository enforces via
        // LibraryUsers. A leaked folder id from someone else's playback URL must
        // not serve bytes to an account scoped to a different library.
        (FolderAccessMiddleware middleware, Func<bool> nextCalled) = Build();
        HttpContext context = BuildContext(
            $"/{OtherFolderId}/some-file.mkv",
            user: AuthenticatedWithSub(GrantedUserId.ToString())
        );

        await middleware.InvokeAsync(context);

        nextCalled().Should().BeFalse();
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.Forbidden);
        (await AuthErrorOf(context)).Should().Be("LIBRARY_ACCESS");
    }

    [Fact]
    public async Task Allows_WhenAllowedUserHoldsAGrantCoveringTheFolder()
    {
        (FolderAccessMiddleware middleware, Func<bool> nextCalled) = Build();
        HttpContext context = BuildContext(
            $"/{GrantedFolderId}/some-file.mkv",
            user: AuthenticatedWithSub(GrantedUserId.ToString())
        );

        await middleware.InvokeAsync(context);

        nextCalled().Should().BeTrue();
    }

    [Fact]
    public async Task Allows_AfterGrantRefresh_WhenLibraryWasGrantedLater()
    {
        // Granting a library through the dashboard refreshes the folder cache;
        // the middleware must honour the new grant without a restart.
        await using (MediaContext ctx = new(_dbOptions))
        {
            ctx.LibraryUser.Add(new LibraryUser(OtherLibraryId, GrantedUserId));
            await ctx.SaveChangesAsync();
            await UserCache.Current.RefreshFolderIdsAsync(ctx);
        }

        (FolderAccessMiddleware middleware, Func<bool> nextCalled) = Build();
        HttpContext context = BuildContext(
            $"/{OtherFolderId}/some-file.mkv",
            user: AuthenticatedWithSub(GrantedUserId.ToString())
        );

        await middleware.InvokeAsync(context);

        nextCalled().Should().BeTrue();
    }

    // =========================================================================
    // Loopback self-ingest: ffmpeg pulling a source with a scoped key
    // =========================================================================

    [Fact]
    public async Task Allows_WhenValidIngestKeyOnIngestPortFromLoopback_NoBearerNoUser()
    {
        LiveIngestKeyStore store = new();
        string path = $"/{GrantedFolderId}/some-file.mkv";
        string key = store.Issue(path);
        (FolderAccessMiddleware middleware, Func<bool> nextCalled) = Build(store);

        await middleware.InvokeAsync(
            BuildContext(path, ingestKey: key, remoteIp: IPAddress.Loopback, localPort: IngestPort)
        );

        // A loopback ffmpeg self-ingest on the internal ingest port carries only the
        // scoped key, no bearer and no authenticated user — it must still be served.
        nextCalled().Should().BeTrue();
    }

    [Fact]
    public async Task Denies_WhenValidIngestKeyArrivesOnPublicPort()
    {
        LiveIngestKeyStore store = new();
        string path = $"/{GrantedFolderId}/some-file.mkv";
        string key = store.Issue(path);
        (FolderAccessMiddleware middleware, Func<bool> nextCalled) = Build(store);

        // The cloudflared-relay case: a valid key from a loopback source IP, but the
        // request landed on the PUBLIC port (relayed external traffic), not the
        // loopback-only ingest port. The bypass must not fire.
        HttpContext context = BuildContext(
            path,
            ingestKey: key,
            remoteIp: IPAddress.Loopback,
            localPort: RuntimeServerSettings.Current.InternalServerPort
        );

        await middleware.InvokeAsync(context);

        nextCalled().Should().BeFalse();
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Denies_WhenValidIngestKeyButNotFromLoopback()
    {
        LiveIngestKeyStore store = new();
        string path = $"/{GrantedFolderId}/some-file.mkv";
        string key = store.Issue(path);
        (FolderAccessMiddleware middleware, Func<bool> nextCalled) = Build(store);

        HttpContext context = BuildContext(
            path,
            ingestKey: key,
            remoteIp: IPAddress.Parse("203.0.113.7"),
            localPort: IngestPort
        );

        await middleware.InvokeAsync(context);

        nextCalled().Should().BeFalse();
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Denies_WhenIngestKeyBoundToDifferentFolder()
    {
        LiveIngestKeyStore store = new();
        string key = store.Issue($"/{GrantedFolderId}/ShowA/Ep/Ep.NoMercy.m3u8");
        (FolderAccessMiddleware middleware, Func<bool> nextCalled) = Build(store);

        // Valid key, loopback, ingest port, but a different title's folder than it
        // was minted for — the key authorizes its own folder subtree, not a sibling.
        HttpContext context = BuildContext(
            $"/{GrantedFolderId}/ShowB/Ep/Ep.NoMercy.m3u8",
            ingestKey: key,
            remoteIp: IPAddress.Loopback,
            localPort: IngestPort
        );

        await middleware.InvokeAsync(context);

        nextCalled().Should().BeFalse();
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Allows_NestedResourceUnderIngestKeyFolder()
    {
        LiveIngestKeyStore store = new();
        // Minted for the encoded master; ffmpeg then self-ingests the nested video
        // variant playlist under the same folder.
        string key = store.Issue($"/{GrantedFolderId}/Show/Ep/Ep.NoMercy.m3u8");
        (FolderAccessMiddleware middleware, Func<bool> nextCalled) = Build(store);

        await middleware.InvokeAsync(
            BuildContext(
                $"/{GrantedFolderId}/Show/Ep/video_1920x1080_SDR/video_1920x1080_SDR.m3u8",
                ingestKey: key,
                remoteIp: IPAddress.Loopback,
                localPort: IngestPort
            )
        );

        nextCalled().Should().BeTrue();
    }

    [Fact]
    public async Task Denial_LogsTheRequestedPathOnASingleLine()
    {
        CapturingLogger logger = new();
        FolderAccessMiddleware middleware = new(
            _ => Task.CompletedTask,
            UserCache.Current,
            new LiveIngestKeyStore(),
            logger
        );

        await middleware.InvokeAsync(
            BuildContext($"/{GrantedFolderId}/x\nInfo: access granted for everyone")
        );

        logger.Messages.Should().ContainSingle();
        logger.Messages[0].Should().NotContain("\n").And.NotContain("\r");
    }

    private sealed class CapturingLogger : ILogger<FolderAccessMiddleware>
    {
        public List<string> Messages { get; } = [];

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Messages.Add(formatter(state, exception));

        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;
    }
}
