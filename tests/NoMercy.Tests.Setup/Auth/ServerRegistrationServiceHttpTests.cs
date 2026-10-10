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

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using NoMercy.Database;
using NoMercy.Database.Models.Users;
using NoMercy.Networking.Certificate;
using NoMercy.Networking.Discovery;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Status;
using NoMercy.Setup.Auth;
using NoMercy.Setup.Dto;
using NoMercy.Tests.Setup.Infrastructure;

namespace NoMercy.Tests.Setup.Auth;

/// <summary>
/// Requirement: registering a self-hosted server with nomercy-tv must retry
/// transient failures, stop immediately on a 401 (re-auth is required, not more
/// retries), share a single in-flight attempt across concurrent callers, and enter
/// a cooldown after a failure so a boot loop cannot hammer the registration API.
/// </summary>
/// <remarks>
/// <see cref="ServerRegistrationService"/> builds its own <c>GenericHttpClient</c>
/// pointed at <c>ExternalServicesConfig.Current.ApiServerBaseUrl</c> rather than
/// accepting an injectable client — a real loopback <see cref="LoopbackHttpServer"/>
/// exercises the actual HTTP contract without a live network dependency.
/// Tests use <c>maxRetries: 1</c> wherever the outcome isn't the retry behavior itself,
/// since the real backoff schedule (2s/5s/15s/30s/60s) uses genuine <see cref="Task.Delay"/>
/// with no injectable clock.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class ServerRegistrationServiceHttpTests : IDisposable
{
    private readonly List<SqliteConnection> _connections = [];

    public void Dispose()
    {
        foreach (SqliteConnection connection in _connections)
            connection.Dispose();
    }

    private Microsoft.EntityFrameworkCore.IDbContextFactory<AppDbContext> CreateFactory()
    {
        SqliteConnection connection = new("DataSource=:memory:;Foreign Keys=False");
        connection.Open();
        _connections.Add(connection);

        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        using (AppDbContext init = new(options))
            init.Database.EnsureCreated();

        Mock<Microsoft.EntityFrameworkCore.IDbContextFactory<AppDbContext>> mock = new();
        mock.Setup(x => x.CreateDbContext()).Returns(() => new(options));
        mock.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(options));
        return mock.Object;
    }

    private static ServerRegistrationService Build(
        Microsoft.EntityFrameworkCore.IDbContextFactory<AppDbContext> factory,
        IUserProvisioningService? provisioning = null,
        ICertificateService? certificate = null,
        IConnectivityStatus? connectivity = null,
        INetworkDiscovery? networkDiscovery = null
    )
    {
        Mock<ICertificateService> certMock = new();
        certMock
            .Setup(c => c.RenewSslCertificate(It.IsAny<string?>(), It.IsAny<int>()))
            .Returns(Task.CompletedTask);

        return new(
            new AuthTokenStore(),
            factory,
            provisioning ?? Mock.Of<IUserProvisioningService>(),
            connectivity ?? new ConnectivityStatus(),
            certificate ?? certMock.Object,
            networkDiscovery
        );
    }

    private static string ServerRegisterResponseJson(string status = "ok") =>
        JsonConvert.SerializeObject(
            new ServerRegisterResponse
            {
                Data = new()
                {
                    Status = status,
                    User = new()
                    {
                        Id = Guid.NewGuid(),
                        Name = "Owner",
                        Email = "owner@nomercy.tv",
                    },
                },
            }
        );

    // ── AssignServer / AssignServerWithRetry ────────────────────────────────

    [Fact]
    public async Task AssignServerWithRetry_Success_ProvisionsOwnerOnce()
    {
        Mock<IUserProvisioningService> provisioning = new();
        provisioning.Setup(p => p.ProvisionOwner(It.IsAny<User>())).Returns(Task.CompletedTask);

        using LoopbackHttpServer server = new();
        server.Handler = req =>
            req.Path.EndsWith("/assign") ? new(200, ServerRegisterResponseJson()) : new(404, "");
        using ExternalServicesConfigScope scope = new(apiServerBaseUrl: server.BaseUrl);

        ServerRegistrationService service = Build(CreateFactory(), provisioning.Object);

        // Init() runs RegisterServer -> AssignServerWithRetry -> RenewSslCertificate.
        // The register call also hits /register on the same server; make it succeed too.
        server.Handler = req => new(200, ServerRegisterResponseJson());

        await service.Init(maxRetries: 1);

        provisioning.Verify(p => p.ProvisionOwner(It.IsAny<User>()), Times.Once);
    }

    [Fact]
    public async Task AssignServer_ErrorStatus_ThrowsFailedToAssign()
    {
        using LoopbackHttpServer server = new();
        server.Handler = req =>
            req.Path.EndsWith("/register")
                ? new(200, ServerRegisterResponseJson())
                : new(200, ServerRegisterResponseJson(status: "error"));
        using ExternalServicesConfigScope scope = new(apiServerBaseUrl: server.BaseUrl);

        ServerRegistrationService service = Build(CreateFactory());

        // AssignServer's `throw new("Failed to assign Server")` is a plain Exception
        // (no explicit type before the target-typed `new`) — not InvalidOperationException.
        Exception ex = await Assert.ThrowsAsync<Exception>(() => service.Init(maxRetries: 1));
        Assert.Equal("Failed to assign Server", ex.Message);
    }

    [Fact]
    public async Task AssignServerWithRetry_401Unauthorized_StopsWithoutExhaustingRetries()
    {
        int assignAttempts = 0;
        using LoopbackHttpServer server = new();
        server.Handler = req =>
        {
            if (req.Path.EndsWith("/register"))
                return new(200, ServerRegisterResponseJson());
            Interlocked.Increment(ref assignAttempts);
            return new(401, "unauthorized");
        };
        using ExternalServicesConfigScope scope = new(apiServerBaseUrl: server.BaseUrl);

        ServerRegistrationService service = Build(CreateFactory());

        // maxRetries: 3 would otherwise sleep through the real 2s/5s backoff between
        // attempts; the 401 short-circuit means only ONE assign attempt should ever
        // fire regardless of the retry budget — and it must FAIL the Init call.
        // The old silent `break` let boot continue to certificate fetch with the
        // same dead token, producing a 30-attempt 401 retry storm downstream.
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.Init(maxRetries: 3)
        );

        Assert.Contains("401", ex.Message);
        Assert.Equal(1, assignAttempts);
    }

    // ── RegisterServer 401 short-circuit ─────────────────────────────────────

    [Fact]
    public async Task RegisterServer_401Unauthorized_StopsWithoutExhaustingRetries()
    {
        int registerAttempts = 0;
        using LoopbackHttpServer server = new();
        server.Handler = req =>
        {
            if (req.Path.EndsWith("/register"))
                Interlocked.Increment(ref registerAttempts);
            return new(401, "unauthorized");
        };
        using ExternalServicesConfigScope scope = new(apiServerBaseUrl: server.BaseUrl);

        ServerRegistrationService service = Build(CreateFactory());

        // A 401 on register is terminal for the token: exactly one attempt fires
        // and Init fails loudly so the caller re-enters auth. Needs maxRetries >= 2
        // so the `when (attempt < maxRetries)` filter is the code path under test
        // (with maxRetries=1 the raw HttpRequestException propagates instead —
        // covered by RegisterServer_SingleAttemptFailure_PropagatesException).
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.Init(maxRetries: 3)
        );

        Assert.Contains("401", ex.Message);
        Assert.Equal(1, registerAttempts);
    }

    [Fact]
    public async Task RegisterServer_SingleAttemptFailure_PropagatesException()
    {
        using LoopbackHttpServer server = new();
        server.Handler = _ => new(400, "bad request");
        using ExternalServicesConfigScope scope = new(apiServerBaseUrl: server.BaseUrl);

        ServerRegistrationService service = Build(CreateFactory());

        // maxRetries: 1 means the `when (attempt < maxRetries)` catch filter never
        // matches on the only attempt, so the underlying HTTP exception propagates
        // out of RegisterServer -> RunRegistrationAsync -> Init unchanged.
        await Assert.ThrowsAsync<HttpRequestException>(() => service.Init(maxRetries: 1));
    }

    // ── Retry-then-succeed (accepts one real ~2s backoff wait) ───────────────

    [Fact]
    public async Task RegisterServer_TransientFailureThenSuccess_Retries()
    {
        int attempt = 0;
        using LoopbackHttpServer server = new();
        server.Handler = req =>
        {
            if (!req.Path.EndsWith("/register"))
                return new(200, ServerRegisterResponseJson());

            int thisAttempt = Interlocked.Increment(ref attempt);
            // 400 (not 500/429/408): GenericHttpClient's own Polly policy retries
            // transient 5xx/408/429 internally with its own 2s/4s/8s backoff, which
            // would stack on top of ServerRegistrationService's outer retry and make
            // this test needlessly slow. A 400 fails the inner client immediately,
            // isolating the OUTER retry-then-succeed behavior this test targets.
            return thisAttempt == 1 ? new(400, "bad request") : new(200, "{}");
        };
        using ExternalServicesConfigScope scope = new(apiServerBaseUrl: server.BaseUrl);

        ServerRegistrationService service = Build(CreateFactory());

        await service.Init(maxRetries: 2);

        Assert.Equal(2, attempt);
    }

    // ── Cooldown after failure ──────────────────────────────────────────────

    [Fact]
    public async Task Init_AfterRecentFailure_ThrowsCooldownWithoutCallingNetwork()
    {
        using LoopbackHttpServer server = new();
        server.Handler = _ => new(400, "bad request");
        using ExternalServicesConfigScope scope = new(apiServerBaseUrl: server.BaseUrl);

        ServerRegistrationService service = Build(CreateFactory());

        // First call fails (single attempt, no retry) and starts the cooldown. The
        // underlying HTTP failure propagates unchanged (see the SingleAttemptFailure
        // test above) — what this test locks is the SECOND call's behavior.
        await Assert.ThrowsAsync<HttpRequestException>(() => service.Init(maxRetries: 1));

        int requestsAfterFirstFailure = server.RequestCount;

        // Immediately calling again must hit the cooldown guard, not the network.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Init(maxRetries: 1));

        Assert.Equal(requestsAfterFirstFailure, server.RequestCount);
    }

    // ── Concurrent callers share the in-flight attempt ──────────────────────

    [Fact]
    public async Task Init_ConcurrentCallers_ShareSingleInFlightAttempt()
    {
        using LoopbackHttpServer server = new();
        server.Handler = req =>
        {
            // Slow enough that both callers are guaranteed to observe the same
            // in-flight Task rather than racing to completion before either checks.
            Thread.Sleep(100);
            return new LoopbackResponse(200, ServerRegisterResponseJson());
        };
        using ExternalServicesConfigScope scope = new(apiServerBaseUrl: server.BaseUrl);

        ServerRegistrationService service = Build(CreateFactory());

        Task first = service.Init(maxRetries: 1);
        Task second = service.Init(maxRetries: 1);

        await Task.WhenAll(first, second);

        Assert.Same(first, second);
    }

    // ── GetTunnelAvailability ────────────────────────────────────────────────

    [Fact]
    public async Task GetTunnelAvailability_Allowed_SetsCloudflareTunnelToken()
    {
        using LoopbackHttpServer server = new();
        server.Handler = _ =>
            new(
                200,
                JsonConvert.SerializeObject(
                    new ServerTunnelAvailabilityResponse
                    {
                        Allowed = true,
                        Token = "tunnel-token-1",
                    }
                )
            );
        using ExternalServicesConfigScope scope = new(apiServerBaseUrl: server.BaseUrl);

        ConnectivityStatus status = new();
        ServerRegistrationService service = Build(CreateFactory(), connectivity: status);

        await service.GetTunnelAvailability();

        Assert.Equal("tunnel-token-1", status.CloudflareTunnelToken);
    }

    [Fact]
    public async Task GetTunnelAvailability_NotAllowed_DoesNotSetToken()
    {
        using LoopbackHttpServer server = new();
        server.Handler = _ =>
            new(
                200,
                JsonConvert.SerializeObject(
                    new ServerTunnelAvailabilityResponse { Allowed = false, Token = null }
                )
            );
        using ExternalServicesConfigScope scope = new(apiServerBaseUrl: server.BaseUrl);

        ConnectivityStatus status = new();
        ServerRegistrationService service = Build(CreateFactory(), connectivity: status);

        await service.GetTunnelAvailability();

        Assert.Null(status.CloudflareTunnelToken);
    }

    [Fact]
    public async Task GetTunnelAvailability_NetworkError_DoesNotThrow()
    {
        using ExternalServicesConfigScope scope = new(apiServerBaseUrl: "http://127.0.0.1:1/");

        ConnectivityStatus status = new();
        ServerRegistrationService service = Build(CreateFactory(), connectivity: status);

        // GetTunnelAvailability wraps every failure — must never throw or crash the caller.
        await service.GetTunnelAvailability();

        Assert.Null(status.CloudflareTunnelToken);
    }

    // ── GetDeviceName ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetServerInfo_ServerNameConfigured_UsesConfiguredName()
    {
        Microsoft.EntityFrameworkCore.IDbContextFactory<AppDbContext> factory = CreateFactory();
        await using (AppDbContext seed = await factory.CreateDbContextAsync())
        {
            seed.Configuration.Add(new() { Key = "serverName", Value = "My Custom Server Name" });
            await seed.SaveChangesAsync();
        }

        using LoopbackHttpServer server = new();
        LoopbackRequest? captured = null;
        server.Handler = req =>
        {
            if (req.Path.EndsWith("/register"))
                captured = req;
            return new(200, ServerRegisterResponseJson());
        };
        using ExternalServicesConfigScope scope = new(apiServerBaseUrl: server.BaseUrl);

        ServerRegistrationService service = Build(factory);
        await service.Init(maxRetries: 1);

        Assert.NotNull(captured);
        Assert.Contains("My+Custom+Server+Name", captured!.Body);
    }
}
