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
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using NoMercy.Database;
using NoMercy.Networking.Certificate;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Security;
using NoMercy.Setup.Auth;
using NoMercy.Setup.Boot;
using NoMercy.Setup.Dto;
using NoMercy.Setup.Server;
using NoMercy.Setup.Ui;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Tests.Setup.Infrastructure;
using NoMercyQueue.Workers;
using Xunit.Abstractions;

namespace NoMercy.Tests.Setup.Server;

/// <summary>
/// Requirement: after <c>/setup/device-code</c> hands the user a code, the server must
/// keep polling Keycloak's token endpoint in the background per RFC 8628 — treating
/// <c>authorization_pending</c> as "keep waiting" (not an error), <c>slow_down</c> as
/// "back off, keep polling" (not fatal), any other error code as terminal (stop and
/// surface it), and a genuinely expired device code as a timeout rather than an
/// infinite silent loop. A TRANSIENT network exception mid-poll (a blip) must not end
/// the login — it retries like <c>authorization_pending</c> — while a PERSISTENT
/// failure still gives up after a capped number of consecutive attempts rather than
/// spinning silently past the code's own expiry.
/// </summary>
/// <remarks>
/// Drives the private <c>PollDeviceGrant</c> loop through the public
/// <c>HandleDeviceCode</c> handler (which starts it fire-and-forget) — there is no
/// direct entry point. Each device response's <c>Interval</c> is set to 1 second (the
/// RFC 8628 minimum <c>Math.Clamp</c> floor already enforces >= 1s), so most scenarios
/// here cost only 1-2 real seconds of wall-clock wait.
/// </remarks>
// Every scenario here spins up a real LoopbackHttpServer and polls real
// wall-clock time against it -- a real HTTP round trip, not a deterministic
// unit -- so it was mislabeled as Unit and forced into the "fast tests" job's
// tight budget. It also shares process-wide state
// (ExternalServicesConfig.Current.AuthBaseUrl), which is what
// ProcessWideSetupStateCollection exists to serialize.
[Trait("Category", "Integration")]
[Collection(ProcessWideSetupStateCollection.Name)]
public sealed class SetupEndpointsDeviceGrantPollingTests : IDisposable
{
    private readonly AppDbContext _appContext;
    private readonly AuthManager _authManager;
    private readonly SetupState _setupState;
    private readonly string? _originalAppPath;
    private readonly string _tempAppPath;
    private readonly string? _originalTokenClientId;

    // PollDeviceGrant is spawned fire-and-forget and only exits early via
    // _appStopping.IsCancellationRequested. A bare Mock.Of<IHostApplicationLifetime>()
    // never signals that, so a scenario that doesn't converge before its own
    // assertion times out left the real background poll task running for the
    // rest of the device code's expiry window -- against a loopback server this
    // test had already disposed -- eating thread-pool capacity and mutating
    // shared state for every test that ran after it in the same process. Wired
    // to a real token so Dispose can actually stop it.
    private readonly CancellationTokenSource _appStoppingCts = new();
    private readonly ITestOutputHelper _output;

    public SetupEndpointsDeviceGrantPollingTests(ITestOutputHelper output)
    {
        _output = output;
        SetupTerminalUi.ForceInteractiveForTests = false;

        _originalAppPath = Environment.GetEnvironmentVariable("NOMERCY_APP_PATH");
        _tempAppPath = Path.Combine(Path.GetTempPath(), $"nm-devicegrant-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempAppPath);
        Environment.SetEnvironmentVariable("NOMERCY_APP_PATH", _tempAppPath);
        NoMercy.NmSystem.Information.AppFiles.CreateAppFolders();

        ServiceCollection services = new();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        ServiceProvider provider = services.BuildServiceProvider();
        TokenStore.Initialize(provider);

        DbContextOptionsBuilder<AppDbContext> optionsBuilder = new();
        optionsBuilder.UseSqlite("Data Source=:memory:");
        _appContext = new(optionsBuilder.Options);
        _appContext.Database.OpenConnection();
        _appContext.Database.EnsureCreated();

        _authManager = new(_appContext, new LocalStorageDriver(), new AuthTokenStore());
        _setupState = new();

        using AppDbContext onDisk = new();
        onDisk.Database.EnsureCreated();
        Start.Certificate = new CertificateService(NullLogger<CertificateService>.Instance, null!);

        _originalTokenClientId = ExternalServicesConfig.Current.TokenClientId;
        ExternalServicesConfig.Current.TokenClientId = "nomercy-server";

        CronWorker.SignalDatabaseReady(true);
    }

    public void Dispose()
    {
        _appStoppingCts.Cancel();
        _appStoppingCts.Dispose();
        SetupTerminalUi.ForceInteractiveForTests = null;
        _appContext.Database.CloseConnection();
        _appContext.Dispose();
        Start.Certificate = null;
        ExternalServicesConfig.Current.TokenClientId = _originalTokenClientId ?? "nomercy-server";
        Environment.SetEnvironmentVariable("NOMERCY_APP_PATH", _originalAppPath);
        try
        {
            if (Directory.Exists(_tempAppPath))
                Directory.Delete(_tempAppPath, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private SetupEndpoints BuildEndpoints()
    {
        Mock<IHostApplicationLifetime> lifetime = new();
        lifetime.SetupGet(l => l.ApplicationStopping).Returns(_appStoppingCts.Token);
        return new(
            _setupState,
            _authManager,
            new NoOpRegistrationService(),
            new RealHttpClientFactory(),
            lifetime.Object
        );
    }

    private sealed class RealHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static DefaultHttpContext BuildPostContext(string path)
    {
        DefaultHttpContext context = new();
        context.Request.Method = "POST";
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string DeviceAuthResponseJson(int expiresIn, int interval) =>
        JsonConvert.SerializeObject(
            new DeviceAuthResponse
            {
                UserCode = "ABCD-1234",
                VerificationUri = "https://auth.nomercy.tv/device",
                VerificationUriComplete = "https://auth.nomercy.tv/device?code=ABCD-1234",
                DeviceCode = "device-code-xyz",
                ExpiresIn = expiresIn,
                Interval = interval,
            }
        );

    private sealed class NoOpRegistrationService : IServerRegistrationService
    {
        public Task Init(int maxRetries = 5) => Task.CompletedTask;

        public Task GetTunnelAvailability() => Task.CompletedTask;
    }

    /// <summary>Routes the shared token endpoint by a per-call response queue, since
    /// PollDeviceGrant issues one POST per interval tick to the same URL.</summary>
    private static LoopbackHttpServer BuildPollingServer(
        DeviceAuthResponse deviceResponse,
        Queue<(int Status, string Body)> pollResponses
    )
    {
        LoopbackHttpServer server = new();
        server.Handler = req =>
        {
            // The initial device-code request hits ".../auth/device"; every
            // subsequent poll tick hits the plain token endpoint.
            bool isDeviceCodeRequest = req.Path.EndsWith("/device");
            if (isDeviceCodeRequest)
                return new(200, JsonConvert.SerializeObject(deviceResponse));

            // Every other POST is a poll tick against the token endpoint.
            if (pollResponses.Count == 0)
                return new(400, "{\"error\":\"authorization_pending\"}");

            (int status, string body) = pollResponses.Dequeue();
            return new(status, body);
        };
        return server;
    }

    [Fact]
    public async Task PollDeviceGrant_AuthorizationPendingThenSuccess_StoresTokensAndAuthenticates()
    {
        string jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(
            new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
                issuer: "https://auth.nomercy.tv/realms/NoMercyTV",
                audience: "nomercy-server",
                claims:
                [
                    new(
                        System.Security.Claims.ClaimTypes.NameIdentifier,
                        Guid.NewGuid().ToString()
                    ),
                ],
                notBefore: DateTime.UtcNow.AddMinutes(-5),
                expires: DateTime.UtcNow.AddHours(1)
            )
        );
        string successBody = JsonConvert.SerializeObject(
            new AuthResponse
            {
                AccessToken = jwt,
                RefreshToken = "refresh-1",
                TokenType = "Bearer",
                ExpiresIn = 3600,
            }
        );

        Queue<(int, string)> pollResponses = new();
        pollResponses.Enqueue((400, "{\"error\":\"authorization_pending\"}"));
        pollResponses.Enqueue((200, successBody));

        using LoopbackHttpServer server = BuildPollingServer(
            new()
            {
                UserCode = "ABCD-1234",
                VerificationUri = "https://auth.nomercy.tv/device",
                VerificationUriComplete = "https://auth.nomercy.tv/device?code=ABCD-1234",
                DeviceCode = "device-code-xyz",
                ExpiresIn = 600,
                Interval = 1,
            },
            pollResponses
        );
        using ExternalServicesConfigScope scope = new(authBaseUrl: server.BaseUrl);

        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildPostContext("/setup/device-code");

        await endpoints.HandleRequestAsync(context);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);

        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (_setupState.CurrentPhase < SetupPhase.Authenticated && DateTime.UtcNow < deadline)
            await Task.Delay(100);

        Assert.True(
            _setupState.CurrentPhase >= SetupPhase.Authenticated,
            $"expected at least Authenticated, was {_setupState.CurrentPhase}"
        );
    }

    [Fact]
    public async Task PollDeviceGrant_AccessDenied_TransitionsToUnauthenticatedWithError()
    {
        Queue<(int, string)> pollResponses = new();
        pollResponses.Enqueue(
            (400, "{\"error\":\"access_denied\",\"error_description\":\"User declined\"}")
        );

        using LoopbackHttpServer server = BuildPollingServer(
            new()
            {
                UserCode = "ABCD-1234",
                VerificationUri = "https://auth.nomercy.tv/device",
                VerificationUriComplete = "https://auth.nomercy.tv/device?code=ABCD-1234",
                DeviceCode = "device-code-xyz",
                ExpiresIn = 600,
                Interval = 1,
            },
            pollResponses
        );
        using ExternalServicesConfigScope scope = new(authBaseUrl: server.BaseUrl);

        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildPostContext("/setup/device-code");
        await endpoints.HandleRequestAsync(context);

        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (_setupState.ErrorMessage is null && DateTime.UtcNow < deadline)
            await Task.Delay(100);

        Assert.Equal(SetupPhase.Unauthenticated, _setupState.CurrentPhase);
        Assert.NotNull(_setupState.ErrorMessage);
    }

    [Fact]
    public async Task PollDeviceGrant_SlowDown_BacksOffThenSucceeds()
    {
        string jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(
            new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
                issuer: "https://auth.nomercy.tv/realms/NoMercyTV",
                audience: "nomercy-server",
                claims:
                [
                    new(
                        System.Security.Claims.ClaimTypes.NameIdentifier,
                        Guid.NewGuid().ToString()
                    ),
                ],
                notBefore: DateTime.UtcNow.AddMinutes(-5),
                expires: DateTime.UtcNow.AddHours(1)
            )
        );
        string successBody = JsonConvert.SerializeObject(
            new AuthResponse
            {
                AccessToken = jwt,
                RefreshToken = "refresh-1",
                TokenType = "Bearer",
                ExpiresIn = 3600,
            }
        );

        Queue<(int, string)> pollResponses = new();
        pollResponses.Enqueue((400, "{\"error\":\"slow_down\"}"));
        pollResponses.Enqueue((200, successBody));

        using LoopbackHttpServer server = BuildPollingServer(
            new()
            {
                UserCode = "ABCD-1234",
                VerificationUri = "https://auth.nomercy.tv/device",
                VerificationUriComplete = "https://auth.nomercy.tv/device?code=ABCD-1234",
                DeviceCode = "device-code-xyz",
                ExpiresIn = 600,
                Interval = 1,
            },
            pollResponses
        );
        using ExternalServicesConfigScope scope = new(authBaseUrl: server.BaseUrl);

        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildPostContext("/setup/device-code");
        await endpoints.HandleRequestAsync(context);

        // slow_down clamps the NEXT interval to +5s (1 -> 6) before retrying, so this
        // scenario genuinely costs several real seconds.
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (_setupState.CurrentPhase < SetupPhase.Authenticated && DateTime.UtcNow < deadline)
            await Task.Delay(200);

        Assert.True(
            _setupState.CurrentPhase >= SetupPhase.Authenticated,
            $"expected at least Authenticated, was {_setupState.CurrentPhase}"
        );
    }

    [Fact]
    public async Task PollDeviceGrant_PersistentNetworkException_RetriesThenTransitionsToUnauthenticatedWithError()
    {
        // Serve the device-code request normally, but abort the connection (a real
        // network-level failure, not a parseable HTTP error) for every poll tick
        // against the token endpoint — deterministic, no scope-swap race against the
        // fire-and-forget background poll task. A single blip must not end the login
        // (see the recovery test below); this covers the OTHER half of that fix — a
        // persistently dead IdP must still give up eventually rather than spin past
        // the device code's own RFC 8628 expiry in silence.
        using LoopbackHttpServer server = new();
        server.Handler = req =>
            req.Path.EndsWith("/device")
                ? new(200, DeviceAuthResponseJson(expiresIn: 600, interval: 1))
                : LoopbackResponse.Aborted();
        using ExternalServicesConfigScope scope = new(authBaseUrl: server.BaseUrl);

        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildPostContext("/setup/device-code");
        await endpoints.HandleRequestAsync(context);

        // Root-caused a real bug (fixed in SetupEndpoints.cs, PollDeviceGrant):
        // consecutiveTransientFailures reset to 0 the instant PostAsync returned,
        // BEFORE the body was read, so a connection that failed between headers
        // and body could never accumulate past 1 -- a persistently dead IdP
        // polled silently forever. But the last two CI runs (30s, then 60s
        // budget) BOTH still failed at just over their own deadline (32s, 65s),
        // which is exactly as consistent with "still not converging" as with
        // "converging under variance" -- inconclusive without seeing whether the
        // wait actually resolves before its own deadline. Logging every tick so
        // the next run answers that with real data instead of another
        // deadline-size guess.
        DateTime start = DateTime.UtcNow;
        DateTime deadline = start.AddSeconds(60);
        DateTime nextLog = start;
        while (_setupState.ErrorMessage is null && DateTime.UtcNow < deadline)
        {
            if (DateTime.UtcNow >= nextLog)
            {
                nextLog = DateTime.UtcNow.AddSeconds(2);
                _output.WriteLine(
                    $"[{(DateTime.UtcNow - start).TotalSeconds:F1}s] waiting: phase={_setupState.CurrentPhase}, requests={server.RequestCount}"
                );
            }
            await Task.Delay(100);
        }
        _output.WriteLine(
            $"[{(DateTime.UtcNow - start).TotalSeconds:F1}s] ended: phase={_setupState.CurrentPhase}, error={_setupState.ErrorMessage ?? "(null)"}, requests={server.RequestCount}"
        );

        Assert.Equal(SetupPhase.Unauthenticated, _setupState.CurrentPhase);
        Assert.NotNull(_setupState.ErrorMessage);
    }

    [Fact]
    public async Task PollDeviceGrant_TransientBlipThenRecovery_DoesNotEndLogin_StoresTokensAndAuthenticates()
    {
        // A brief network blip (a couple of aborted connections, standing in for a
        // ~5s outage at the 1s poll interval used here) followed by the IdP coming
        // back must NOT end the device login — this is the defect the retry/backoff
        // fix addresses: a transient exception used to permanently abort the ONLY
        // Docker/NAS login path there is.
        string jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(
            new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
                issuer: "https://auth.nomercy.tv/realms/NoMercyTV",
                audience: "nomercy-server",
                claims:
                [
                    new(
                        System.Security.Claims.ClaimTypes.NameIdentifier,
                        Guid.NewGuid().ToString()
                    ),
                ],
                notBefore: DateTime.UtcNow.AddMinutes(-5),
                expires: DateTime.UtcNow.AddHours(1)
            )
        );
        string successBody = JsonConvert.SerializeObject(
            new AuthResponse
            {
                AccessToken = jwt,
                RefreshToken = "refresh-1",
                TokenType = "Bearer",
                ExpiresIn = 3600,
            }
        );

        int tokenPollAttempts = 0;
        using LoopbackHttpServer server = new();
        server.Handler = req =>
        {
            if (req.Path.EndsWith("/device"))
                return new(200, DeviceAuthResponseJson(expiresIn: 600, interval: 1));

            int attempt = Interlocked.Increment(ref tokenPollAttempts);
            // First two ticks: a transient network-level failure (the blip).
            // Third tick onward: the IdP is back — succeed.
            return attempt <= 2 ? LoopbackResponse.Aborted() : new(200, successBody);
        };
        using ExternalServicesConfigScope scope = new(authBaseUrl: server.BaseUrl);

        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildPostContext("/setup/device-code");
        await endpoints.HandleRequestAsync(context);

        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (_setupState.CurrentPhase < SetupPhase.Authenticated && DateTime.UtcNow < deadline)
            await Task.Delay(100);

        Assert.True(
            _setupState.CurrentPhase >= SetupPhase.Authenticated,
            $"expected at least Authenticated, was {_setupState.CurrentPhase} (error: {_setupState.ErrorMessage})"
        );
        Assert.True(
            tokenPollAttempts > 2,
            "expected the loop to survive the blip and keep polling"
        );
    }

    [Fact]
    public async Task PollDeviceGrant_AlreadyAuthenticatedBeforeFirstTick_StopsWithoutPolling()
    {
        // If the browser SSO flow completes first (silent SSO racing the device
        // code), the poll loop's own guard must notice and stop rather than
        // clobbering an already-authenticated state.
        int pollCount = 0;
        using LoopbackHttpServer server = new();
        server.Handler = req =>
        {
            if (req.Path.EndsWith("/device"))
                return new(200, DeviceAuthResponseJson(expiresIn: 600, interval: 1));
            Interlocked.Increment(ref pollCount);
            return new(400, "{\"error\":\"authorization_pending\"}");
        };
        using ExternalServicesConfigScope scope = new(authBaseUrl: server.BaseUrl);

        SetupEndpoints endpoints = BuildEndpoints();
        DefaultHttpContext context = BuildPostContext("/setup/device-code");
        await endpoints.HandleRequestAsync(context);

        // Immediately mark authenticated via the browser-SSO path before the first
        // poll tick (1s later) fires.
        _setupState.TransitionTo(SetupPhase.Authenticating);
        _setupState.TransitionTo(SetupPhase.Authenticated);

        await Task.Delay(1500);

        Assert.Equal(0, pollCount);
    }
}
