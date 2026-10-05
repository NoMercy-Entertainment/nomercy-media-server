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

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using NoMercy.Database;
using NoMercy.Networking.Certificate;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.Security;
using NoMercy.Setup.Auth;
using NoMercy.Setup.Boot;
using NoMercy.Setup.Dto;
using NoMercy.Setup.Server;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Tests.Setup.Infrastructure;

namespace NoMercy.Tests.Setup.Integration;

/// <summary>
/// Pins the complete first-boot onboarding chain, recorded live on 2026-08-01 from
/// the v0.1.450 linux release binary onboarding a brand-new account against the real
/// dev stack (trace: .claude/work/onboarding/2026-08-01-linux-binary-first-boot-trace.md):
///
///   1. device-code grant   — POST auth/device → poll token (authorization_pending → tokens)
///   2. token storage       — StoreTokensAsync persists the refresh token, SetupState advances
///   3. registration        — POST register → POST assign (owner provisioned)
///   4. certificate         — GET certificate?id → 202 → 202 → 200 envelope → DB rows
///   5. phase machine       — Registering → Registered → CertificateAcquired → Complete
///   6. HTTPS restart       — a FRESH CertificateService (new DI container, empty cache)
///                            must serve the acquired cert on a REAL TLS handshake
///
/// Every stage runs the real production component over real loopback HTTP against
/// fakes speaking the recorded wire contract. Stage 6 is a real Kestrel socket
/// handshake through the production listener config — the exact layer that shipped
/// broken twice (silent-401 retry storm; "No SSL certificate loaded" after restart).
/// If any link in this chain regresses, this test fails; it must never be weakened
/// to pass.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FirstBootOnboardingChainTests : IDisposable
{
    private static readonly string[] ChainConfigKeys =
    [
        "auth_refresh_token",
        "ssl_certificate",
        "ssl_private_key",
        "ssl_ca",
    ];

    private readonly AppDbContext _dbContext;

    public FirstBootOnboardingChainTests()
    {
        ServiceCollection services = new();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        TokenStore.Initialize(services.BuildServiceProvider());

        Directory.CreateDirectory(AppFiles.DataPath);
        // Production creates this via AppFiles.CreateAppFolders before any cert
        // fetch; FetchCertificate writes backwards-compat PEM files here.
        Directory.CreateDirectory(Path.Combine(AppFiles.AppPath, "security", "certs"));
        _dbContext = new();
        _dbContext.Database.EnsureCreated();
        RemoveChainConfigRows();
    }

    public void Dispose()
    {
        RemoveChainConfigRows();
        // FetchCertificate also writes backwards-compat PEM files; leaving them
        // behind makes HasValidCertificate() true for every later test in the
        // assembly via the legacy-file fallback.
#pragma warning disable CS0618
        foreach (string file in (string[])[AppFiles.CertFile, AppFiles.KeyFile, AppFiles.CaFile])
            if (File.Exists(file))
                File.Delete(file);
#pragma warning restore CS0618
        _dbContext.Dispose();
    }

    private void RemoveChainConfigRows()
    {
        _dbContext.Configuration.RemoveRange(
            _dbContext.Configuration.Where(c => ChainConfigKeys.Contains(c.Key))
        );
        _dbContext.SaveChanges();
        _dbContext.ChangeTracker.Clear();
    }

    // ── Fakes speaking the recorded wire contract ───────────────────────────

    private static string UnsignedJwt()
    {
        JwtSecurityTokenHandler handler = new();
        JwtSecurityToken token = new(
            issuer: "https://auth.nomercy.tv/realms/NoMercyTV",
            audience: "nomercy-server",
            claims: [new("sub", Guid.NewGuid().ToString())],
            notBefore: DateTime.UtcNow.AddMinutes(-5),
            expires: DateTime.UtcNow.AddHours(1)
        );
        return handler.WriteToken(token);
    }

    private static (string CertPem, string KeyPem, string Subject) IssueChainCertificate()
    {
        const string subject = "CN=onboarding-chain.nomercy.tv";
        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new(
            subject,
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        using X509Certificate2 cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(60)
        );
        return (cert.ExportCertificatePem(), rsa.ExportRSAPrivateKeyPem(), subject);
    }

    /// <summary>
    /// The 202→202→200 issuance sequence recorded from nomercy-tv: while the ACME
    /// order runs the endpoint answers 202 Accepted; the third poll returns the
    /// certificate envelope.
    /// </summary>
    private static LoopbackResponse CertificateEndpointResponse(
        int pollNumber,
        string certPem,
        string keyPem
    )
    {
        if (pollNumber < 3)
            return new(202, "{\"status\":\"pending\"}");

        return new(
            200,
            JsonConvert.SerializeObject(
                new
                {
                    status = "ok",
                    data = new
                    {
                        status = "ok",
                        certificate = certPem,
                        private_key = keyPem,
                        issuer_certificate = string.Empty,
                        certificate_authority = string.Empty,
                    },
                }
            )
        );
    }

    private sealed class DefaultPathDbContextFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new();
    }

    /// <summary>
    /// The real CertificateService with only the 10s inter-poll sleep removed —
    /// identical logic, test-speed retries (same pattern as NoDelayCertificateService
    /// in CertificateServiceValidationTests).
    /// </summary>
    private sealed class FastRetryCertificateService(IHttpClientFactory factory)
        : CertificateService(NullLogger<CertificateService>.Instance, factory)
    {
        protected override Task DelayBetweenAttemptsAsync(TimeSpan delay, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class RealHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    // ── The chain ───────────────────────────────────────────────────────────

    [Fact]
    public async Task FirstBoot_FullOnboardingChain_DeviceGrantToServedHttps()
    {
        (string certPem, string keyPem, string certSubject) = IssueChainCertificate();
        List<string> apiCalls = [];
        int certificatePolls = 0;
        int tokenPolls = 0;

        // ── Fake Keycloak: device grant endpoints, recorded contract ────────
        using LoopbackHttpServer keycloak = new();
        keycloak.Handler = req =>
        {
            if (req.Path.EndsWith("/.well-known/openid-configuration"))
                return new(200, "{}");

            if (req.Path.EndsWith("/protocol/openid-connect/auth/device"))
                return new(
                    200,
                    JsonConvert.SerializeObject(
                        new
                        {
                            device_code = "chain-device-code",
                            user_code = "AAAA-BBBB",
                            verification_uri = "https://auth.example/device",
                            verification_uri_complete = "https://auth.example/device?user_code=AAAA-BBBB",
                            expires_in = 600,
                            interval = 0,
                        }
                    )
                );

            if (req.Path.EndsWith("/protocol/openid-connect/token"))
            {
                tokenPolls++;
                // First two polls: the user is still signing in/consenting.
                if (tokenPolls < 3)
                    return new(400, "{\"error\":\"authorization_pending\"}");

                return new(
                    200,
                    JsonConvert.SerializeObject(
                        new AuthResponse
                        {
                            AccessToken = UnsignedJwt(),
                            RefreshToken = "chain-refresh-token",
                            TokenType = "Bearer",
                            ExpiresIn = 3600,
                        }
                    )
                );
            }

            return new(404, "not found");
        };

        // ── Fake nomercy-tv API: register / assign / certificate ────────────
        using LoopbackHttpServer api = new();
        api.Handler = req =>
        {
            lock (apiCalls)
                apiCalls.Add(req.Path.TrimEnd('/').Split('/')[^1]);

            if (req.Path.EndsWith("/register"))
                return new(200, "{\"status\":\"ok\"}");

            if (req.Path.EndsWith("/assign"))
                return new(
                    200,
                    JsonConvert.SerializeObject(
                        new ServerRegisterResponse
                        {
                            Data = new()
                            {
                                Status = "ok",
                                User = new()
                                {
                                    Id = Guid.NewGuid(),
                                    Name = "Chain Owner",
                                    Email = "chain-owner@nomercy.tv",
                                },
                            },
                        }
                    )
                );

            if (req.Path.EndsWith("/certificate"))
                return CertificateEndpointResponse(++certificatePolls, certPem, keyPem);

            return new(404, "not found");
        };

        using ExternalServicesConfigScope scope = new(
            authBaseUrl: keycloak.BaseUrl,
            apiServerBaseUrl: api.BaseUrl
        );

        // ── Stage 1: device-code grant over the recorded endpoints ──────────
        using HttpClient deviceClient = new();
        List<KeyValuePair<string, string>> deviceBody = AuthManager.BuildDeviceCodeRequestBody(
            "nomercy-server"
        );
        Assert.Contains(deviceBody, p => p.Key == "client_id" && p.Value == "nomercy-server");

        using HttpResponseMessage deviceResponse = await deviceClient.PostAsync(
            $"{keycloak.BaseUrl}protocol/openid-connect/auth/device",
            new FormUrlEncodedContent(deviceBody)
        );
        DeviceAuthResponse device = JsonConvert.DeserializeObject<DeviceAuthResponse>(
            await deviceResponse.Content.ReadAsStringAsync()
        )!;
        Assert.Equal("AAAA-BBBB", device.UserCode);
        Assert.False(string.IsNullOrEmpty(device.DeviceCode));

        List<KeyValuePair<string, string>> tokenBody = AuthManager.BuildDeviceTokenBody(
            "nomercy-server",
            device.DeviceCode
        );
        Assert.Contains(
            tokenBody,
            p => p.Key == "grant_type" && p.Value == "urn:ietf:params:oauth:grant-type:device_code"
        );

        AuthResponse? tokens = null;
        for (int poll = 0; poll < 10 && tokens is null; poll++)
        {
            using HttpResponseMessage tokenResponse = await deviceClient.PostAsync(
                $"{keycloak.BaseUrl}protocol/openid-connect/token",
                new FormUrlEncodedContent(tokenBody)
            );
            string json = await tokenResponse.Content.ReadAsStringAsync();

            if (tokenResponse.IsSuccessStatusCode)
            {
                tokens = JsonConvert.DeserializeObject<AuthResponse>(json);
                break;
            }

            // The product poll loop keeps going only on authorization_pending.
            Assert.Contains("authorization_pending", json);
        }

        Assert.NotNull(tokens?.AccessToken);
        Assert.Equal(3, tokenPolls);

        // ── Stage 2: real AuthManager stores the tokens, SetupState advances ─
        AuthTokenStore tokenStore = new();
        AuthManager authManager = new(_dbContext, new LocalStorageDriver(), tokenStore);
        await authManager.StoreTokensAsync(tokens!);

        Assert.Equal(tokens!.AccessToken, tokenStore.AccessToken);
        Assert.NotNull(
            await _dbContext
                .Configuration.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Key == "auth_refresh_token")
        );

        SetupState setupState = new();
        Assert.True(setupState.TransitionTo(SetupPhase.Authenticating));
        Assert.True(setupState.TransitionTo(SetupPhase.Authenticated));

        // ── Stages 3-5: real registration + certificate + phase machine ─────
        FastRetryCertificateService certificateService = new(new RealHttpClientFactory());
        ServerRegistrationService registrationService = new(
            tokenStore,
            new DefaultPathDbContextFactory(),
            Mock.Of<IUserProvisioningService>(),
            new NoMercy.NmSystem.Status.ConnectivityStatus(),
            certificateService,
            null
        );

        BootOrchestrator orchestrator = new(
            setupState,
            authManager,
            new FakeApiKeyLoader(),
            new FakeDegradedModeRecovery(),
            registrationService,
            tokenStore,
            certificateService,
            new RealHttpClientFactory()
        );

        bool certAcquired = await orchestrator.RunRegistrationAsync(CancellationToken.None);

        if (!certAcquired)
            Assert.Fail(
                $"RunRegistrationAsync must report the certificate as acquired "
                    + $"(error: {setupState.ErrorMessage ?? "none"}; api calls: {string.Join(",", apiCalls)})"
            );
        Assert.Equal(SetupPhase.Complete, setupState.CurrentPhase);
        Assert.False(setupState.IsSetupRequired);

        Assert.Equal(["register", "assign", "certificate", "certificate", "certificate"], apiCalls);

        _dbContext.ChangeTracker.Clear();
        Assert.NotNull(
            await _dbContext
                .Configuration.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Key == "ssl_certificate")
        );
        Assert.NotNull(
            await _dbContext
                .Configuration.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Key == "ssl_private_key")
        );

        // ── Stage 6: the HTTPS restart — a FRESH service must serve the cert ─
        // A new DI container starts with an empty in-memory cache; the selector
        // reads only that cache. This is the layer that shipped broken: binding
        // TLS and then throwing "No SSL certificate loaded" on every handshake.
        CertificateService freshContainerService = new(
            NullLogger<CertificateService>.Instance,
            new RealHttpClientFactory()
        );
        Assert.True(freshContainerService.EnsureHttpsCertificate());

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(
                IPAddress.Loopback,
                0,
                listenOptions => freshContainerService.ConfigureHttpsListener(listenOptions)
            )
        );

        await using WebApplication app = builder.Build();
        app.MapGet("/api/v1/status", () => Results.Unauthorized());
        await app.StartAsync();

        try
        {
            string baseUrl = app.Urls.Single();
            Assert.StartsWith("https://", baseUrl);

            X509Certificate2? presented = null;
            using HttpClientHandler tlsCapture = new();
            tlsCapture.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
            {
                presented = cert is null
                    ? null
                    : X509CertificateLoader.LoadCertificate(cert.RawData);
                return true;
            };
            using HttpClient httpsClient = new(tlsCapture);

            using HttpResponseMessage status = await httpsClient.GetAsync(
                $"{baseUrl}/api/v1/status"
            );

            Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
            Assert.NotNull(presented);
            Assert.Equal(certSubject, presented!.Subject);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    /// <summary>
    /// A failed registration must reach a distinct, retryable Failed phase — never
    /// the misleading Complete a degraded boot used to reach, which the setup page
    /// rendered as "Setup complete!" with no error, no retry, no server URL.
    /// TransitionTo clears the error message as stale-progress cleanup, so
    /// recording the error before the Failed transition would silently erase it —
    /// pins the transition-then-error order.
    /// </summary>
    [Fact]
    public async Task FailedRegistration_ReachesDistinctFailedPhase_WithErrorVisible()
    {
        using LoopbackHttpServer api = new();
        api.Handler = _ => new(401, "{\"message\":\"Unauthenticated.\"}");

        using ExternalServicesConfigScope scope = new(apiServerBaseUrl: api.BaseUrl);

        AuthTokenStore tokenStore = new();
        tokenStore.SetAccessToken("dead-token");
        AuthManager authManager = new(_dbContext, new LocalStorageDriver(), tokenStore);

        SetupState setupState = new();
        Assert.True(setupState.TransitionTo(SetupPhase.Authenticating));
        Assert.True(setupState.TransitionTo(SetupPhase.Authenticated));

        FastRetryCertificateService certificateService = new(new RealHttpClientFactory());
        ServerRegistrationService registrationService = new(
            tokenStore,
            new DefaultPathDbContextFactory(),
            Mock.Of<IUserProvisioningService>(),
            new NoMercy.NmSystem.Status.ConnectivityStatus(),
            certificateService,
            null
        );

        BootOrchestrator orchestrator = new(
            setupState,
            authManager,
            new FakeApiKeyLoader(),
            new FakeDegradedModeRecovery(),
            registrationService,
            tokenStore,
            certificateService,
            new RealHttpClientFactory()
        );

        bool certAcquired = await orchestrator.RunRegistrationAsync(CancellationToken.None);

        Assert.False(certAcquired);
        Assert.Equal(SetupPhase.Failed, setupState.CurrentPhase);
        Assert.True(setupState.IsSetupRequired);
        Assert.NotNull(setupState.ErrorMessage);
        Assert.Contains("Registration failed", setupState.ErrorMessage);
    }
}
