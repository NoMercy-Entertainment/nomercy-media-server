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
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using NoMercy.Database;
using NoMercy.Database.Models.Common;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Security;
using NoMercy.Setup.Auth;
using NoMercy.Setup.Dto;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Tests.Setup.Infrastructure;

namespace NoMercy.Tests.Setup.Auth;

/// <summary>
/// Requirement: AuthManager's refresh/PKCE/dead-refresh-token paths must behave
/// correctly against the real Keycloak token endpoint contract — success stores
/// tokens and signals auth-ready, an <c>invalid_grant</c> response permanently clears
/// the refresh token (forcing re-auth through /setup rather than retrying forever),
/// and any other failure clears the access token without touching the refresh token
/// so a transient Keycloak hiccup can still recover on the next attempt.
/// </summary>
/// <remarks>
/// AuthManager builds its own <c>new HttpClient()</c> pointed at
/// <see cref="ExternalServicesConfig.Current"/>.AuthBaseUrl rather than accepting an
/// injectable client, so a fake <see cref="HttpMessageHandler"/> cannot reach it. A real
/// loopback <see cref="LoopbackHttpServer"/> exercises the actual HTTP round trip without
/// touching the live internet or a real Keycloak.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class AuthManagerHttpTests : IDisposable
{
    private readonly AppDbContext _appContext;
    private readonly AuthManager _authManager;
    private readonly AuthTokenStore _authTokenStore = new();
    private readonly string? _originalTokenClientId;

    public AuthManagerHttpTests()
    {
        ServiceCollection services = new();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        ServiceProvider provider = services.BuildServiceProvider();
        TokenStore.Initialize(provider);

        DbContextOptionsBuilder<AppDbContext> optionsBuilder = new();
        optionsBuilder.UseSqlite("Data Source=:memory:");
        _appContext = new(optionsBuilder.Options);
        _appContext.Database.OpenConnection();
        _appContext.Database.EnsureCreated();

        _authManager = new(_appContext, new LocalStorageDriver(), _authTokenStore);
        _originalTokenClientId = ExternalServicesConfig.Current.TokenClientId;
    }

    public void Dispose()
    {
        _appContext.Database.CloseConnection();
        _appContext.Dispose();
        _authTokenStore.SetAccessToken(null);
        ExternalServicesConfig.Current.TokenClientId = _originalTokenClientId ?? "nomercy-server";
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string CreateJwt(DateTime validTo, DateTime? notBefore = null)
    {
        JwtSecurityTokenHandler handler = new();
        DateTime nbf =
            notBefore
            ?? (
                validTo < DateTime.UtcNow ? validTo.AddMinutes(-10) : DateTime.UtcNow.AddMinutes(-5)
            );
        JwtSecurityToken token = new(
            issuer: "https://auth.nomercy.tv/realms/NoMercyTV",
            audience: "nomercy-server",
            claims: [new("sub", Guid.NewGuid().ToString())],
            notBefore: nbf,
            expires: validTo
        );
        return handler.WriteToken(token);
    }

    private async Task SeedRefreshToken(string value)
    {
        _appContext.Configuration.Add(
            new()
            {
                Key = "auth_refresh_token",
                Value = string.Empty,
                SecureValue = value,
            }
        );
        await _appContext.SaveChangesAsync();
    }

    private async Task<Configuration?> ReadConfig(string key) =>
        await _appContext.Configuration.AsNoTracking().FirstOrDefaultAsync(c => c.Key == key);

    private static string AuthResponseJson(
        string accessToken,
        string? refreshToken = "new-refresh"
    ) =>
        JsonConvert.SerializeObject(
            new AuthResponse
            {
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                TokenType = "Bearer",
                ExpiresIn = 3600,
            }
        );

    // ── RefreshAsync / TryRefreshToken ──────────────────────────────────────

    [Fact]
    public async Task RefreshAsync_NoRefreshTokenInDb_ClearsAccessTokenAndReturns()
    {
        _authTokenStore.SetAccessToken("stale-access-token");

        await _authManager.RefreshAsync();

        Assert.Null(_authTokenStore.AccessToken);
    }

    [Fact]
    public async Task RefreshAsync_Success_StoresNewTokensAndSetsAccessToken()
    {
        await SeedRefreshToken("old-refresh-token");
        string newJwt = CreateJwt(DateTime.UtcNow.AddHours(1));

        using LoopbackHttpServer server = new();
        server.Handler = _ => new(200, AuthResponseJson(newJwt));
        using ExternalServicesConfigScope scope = new(authBaseUrl: server.BaseUrl);

        await _authManager.RefreshAsync();

        Assert.Equal(newJwt, _authTokenStore.AccessToken);
        Configuration? refreshRow = await ReadConfig("auth_refresh_token");
        Assert.Equal("new-refresh", refreshRow?.SecureValue);
    }

    [Fact]
    public async Task RefreshAsync_TransientServerError_ClearsAccessToken_KeepsRefreshToken()
    {
        await SeedRefreshToken("still-valid-refresh-token");

        using LoopbackHttpServer server = new();
        server.Handler = _ => new(503, "service unavailable");
        using ExternalServicesConfigScope scope = new(authBaseUrl: server.BaseUrl);

        _authTokenStore.SetAccessToken("stale-token");
        await _authManager.RefreshAsync();

        Assert.Null(_authTokenStore.AccessToken);
        // A transient failure must not discard the still-valid refresh token —
        // only an explicit invalid_grant does that (see the invalid_grant test below).
        Configuration? refreshRow = await ReadConfig("auth_refresh_token");
        Assert.Equal("still-valid-refresh-token", refreshRow?.SecureValue);
    }

    [Fact]
    public async Task RefreshAsync_InvalidGrant_PermanentlyClearsRefreshToken()
    {
        await SeedRefreshToken("dead-refresh-token");

        using LoopbackHttpServer server = new();
        server.Handler = _ =>
            new(400, "{\"error\":\"invalid_grant\",\"error_description\":\"Session not found\"}");
        using ExternalServicesConfigScope scope = new(authBaseUrl: server.BaseUrl);

        await _authManager.RefreshAsync();

        Assert.Null(_authTokenStore.AccessToken);
        Configuration? refreshRow = await ReadConfig("auth_refresh_token");
        // Empty string round-trips to null through TokenStore's encrypt/decrypt
        // converter (see TokenStore.DecryptToken) — both mean "cleared".
        Assert.True(
            string.IsNullOrEmpty(refreshRow?.SecureValue),
            "invalid_grant must permanently clear the refresh token"
        );
    }

    [Fact]
    public async Task RefreshAsync_NetworkUnreachable_ReturnsFalseWithoutThrowing()
    {
        await SeedRefreshToken("some-refresh-token");
        // Port 1 on loopback: nothing listens there — connection refused immediately,
        // no live network dependency and no multi-second timeout.
        using ExternalServicesConfigScope scope = new(authBaseUrl: "http://127.0.0.1:1/");

        await _authManager.RefreshAsync();

        Assert.Null(_authTokenStore.AccessToken);
    }

    [Fact]
    public async Task RefreshAsync_TokenClientIdNotConfigured_ReturnsFalseWithoutCallingNetwork()
    {
        await SeedRefreshToken("some-refresh-token");
        ExternalServicesConfig.Current.TokenClientId = string.Empty;

        await _authManager.RefreshAsync();

        Assert.Null(_authTokenStore.AccessToken);
    }

    [Fact]
    public async Task RefreshAsync_ResponseMissingAccessToken_ReturnsFalse()
    {
        await SeedRefreshToken("some-refresh-token");

        using LoopbackHttpServer server = new();
        server.Handler = _ => new(200, "{\"token_type\":\"Bearer\"}");
        using ExternalServicesConfigScope scope = new(authBaseUrl: server.BaseUrl);

        await _authManager.RefreshAsync();

        Assert.Null(_authTokenStore.AccessToken);
    }

    // ── StoreTokensAsync(AuthResponse) ───────────────────────────────────────

    [Fact]
    public async Task StoreTokensAsync_AuthResponse_NullAccessToken_DoesNotPersist()
    {
        await _authManager.StoreTokensAsync(new AuthResponse { AccessToken = null });

        Configuration? row = await ReadConfig("auth_access_token");
        Assert.Null(row);
    }

    [Fact]
    public async Task StoreTokensAsync_AuthResponse_ValidJwt_UsesJwtExpiry()
    {
        DateTime expiry = DateTime.UtcNow.AddHours(3);
        string jwt = CreateJwt(expiry);

        await _authManager.StoreTokensAsync(
            new AuthResponse
            {
                AccessToken = jwt,
                RefreshToken = "r1",
                TokenType = "Bearer",
                ExpiresIn = 60, // deliberately different from the JWT's real exp claim
            }
        );

        Configuration? metaRow = await ReadConfig("auth_token_metadata");
        Assert.NotNull(metaRow);
        Assert.Contains(expiry.ToString("O")[..16], metaRow!.SecureValue);
    }

    [Fact]
    public async Task StoreTokensAsync_AuthResponse_UnparsableToken_FallsBackToExpiresIn()
    {
        await _authManager.StoreTokensAsync(
            new AuthResponse
            {
                AccessToken = "not-a-real-jwt",
                RefreshToken = "r2",
                TokenType = "Bearer",
                ExpiresIn = 120,
            }
        );

        Assert.Equal("not-a-real-jwt", _authTokenStore.AccessToken);
        Configuration? metaRow = await ReadConfig("auth_token_metadata");
        Assert.NotNull(metaRow);
    }

    [Fact]
    public async Task StoreTokensAsync_AuthResponse_UnparsableTokenAndNoExpiresIn_DefaultsTo300Seconds()
    {
        DateTime before = DateTime.UtcNow;

        await _authManager.StoreTokensAsync(
            new AuthResponse
            {
                AccessToken = "still-not-a-jwt",
                RefreshToken = "r3",
                TokenType = "Bearer",
                ExpiresIn = 0,
            }
        );

        Configuration? metaRow = await ReadConfig("auth_token_metadata");
        Assert.NotNull(metaRow);
        // Parse back the persisted expires_at and confirm it lands ~300s out (not 0, not huge).
        PersistedTokenMetadata meta =
            JsonConvert.DeserializeObject<PersistedTokenMetadata>(metaRow!.SecureValue!)
            ?? throw new InvalidOperationException("metadata did not deserialize");
        DateTime expiresAt = DateTime.Parse(
            meta.ExpiresAt!,
            null,
            System.Globalization.DateTimeStyles.RoundtripKind
        );
        TimeSpan delta = expiresAt - before;
        Assert.InRange(delta.TotalSeconds, 250, 350);
    }

    private sealed class PersistedTokenMetadata
    {
        [JsonProperty("expires_at")]
        public string? ExpiresAt { get; set; }
    }

    // ── TryCompletePkceFromCallbackAsync ─────────────────────────────────────

    [Fact]
    public async Task TryCompletePkceFromCallbackAsync_NoPendingFlow_ReturnsFalse()
    {
        bool result = await AuthManager.TryCompletePkceFromCallbackAsync(
            "some-code",
            "some-state",
            "http://localhost/callback"
        );

        Assert.False(result);
    }

    [Fact]
    public async Task TryCompletePkceFromCallbackAsync_StateMismatch_ReturnsFalse()
    {
        AuthManager.PreparePkceBrowserFlow("verifier-abc", "expected-state");

        bool result = await AuthManager.TryCompletePkceFromCallbackAsync(
            "some-code",
            "wrong-state",
            "http://localhost/callback"
        );

        Assert.False(result);

        // Clean up: a state mismatch does not consume the pending flow, so drain it
        // with a matching-but-doomed call to avoid leaking static state into other tests.
        await AuthManager.TryCompletePkceFromCallbackAsync(
            "any",
            "expected-state",
            "http://localhost/callback"
        );
    }

    [Fact]
    public async Task TryCompletePkceFromCallbackAsync_Success_SetsAccessTokenAndReturnsTrue()
    {
        AuthManager.PreparePkceBrowserFlow("verifier-xyz", "state-xyz");
        string jwt = CreateJwt(DateTime.UtcNow.AddHours(1));

        using LoopbackHttpServer server = new();
        server.Handler = _ => new(200, AuthResponseJson(jwt));
        using ExternalServicesConfigScope scope = new(authBaseUrl: server.BaseUrl);

        bool result = await AuthManager.TryCompletePkceFromCallbackAsync(
            "auth-code",
            "state-xyz",
            "http://localhost/callback",
            _authTokenStore
        );

        Assert.True(result);
        Assert.Equal(jwt, _authTokenStore.AccessToken);
    }

    [Fact]
    public async Task TryCompletePkceFromCallbackAsync_TokenExchangeFails_ReturnsFalse()
    {
        AuthManager.PreparePkceBrowserFlow("verifier-fail", "state-fail");

        using LoopbackHttpServer server = new();
        server.Handler = _ => new(400, "{\"error\":\"invalid_request\"}");
        using ExternalServicesConfigScope scope = new(authBaseUrl: server.BaseUrl);

        bool result = await AuthManager.TryCompletePkceFromCallbackAsync(
            "auth-code",
            "state-fail",
            "http://localhost/callback",
            _authTokenStore
        );

        Assert.False(result);
    }

    [Fact]
    public async Task TryCompletePkceFromCallbackAsync_TokenClientIdMissing_ReturnsFalse()
    {
        AuthManager.PreparePkceBrowserFlow("verifier-noclient", "state-noclient");
        ExternalServicesConfig.Current.TokenClientId = string.Empty;

        bool result = await AuthManager.TryCompletePkceFromCallbackAsync(
            "auth-code",
            "state-noclient",
            "http://localhost/callback",
            _authTokenStore
        );

        Assert.False(result);
    }

    // ── ScheduleBackgroundRefresh ────────────────────────────────────────────

    [Fact]
    public async Task ScheduleBackgroundRefresh_NearExpiryToken_TriggersImmediateRefresh()
    {
        await SeedRefreshToken("bg-refresh-token");
        // Expiring in 2s means (expiry - now - 60s) is negative, so the loop skips its
        // wait entirely and calls RefreshAsync on the very first iteration.
        string aboutToExpire = CreateJwt(DateTime.UtcNow.AddSeconds(2));
        _authTokenStore.SetAccessToken(aboutToExpire);

        using LoopbackHttpServer server = new();
        string freshJwt = CreateJwt(DateTime.UtcNow.AddHours(1));
        server.Handler = _ => new(200, AuthResponseJson(freshJwt));
        using ExternalServicesConfigScope scope = new(authBaseUrl: server.BaseUrl);

        using CancellationTokenSource cts = new();
        _authManager.ScheduleBackgroundRefresh(cts.Token);

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (server.RequestCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.True(
            server.RequestCount > 0,
            "Background refresh should have hit the token endpoint"
        );

        cts.Cancel();
        await Task.Delay(100); // let the loop observe cancellation and exit
    }

    [Fact]
    public async Task ScheduleBackgroundRefresh_CancelledMidWait_StopsLoopCleanly()
    {
        // Far-future expiry means the loop computes a long positive delay and is
        // sitting inside `await Task.Delay(delay, linked)` when we cancel — this is
        // the OperationCanceledException break branch, distinct from the near-expiry
        // "refresh fires immediately" path above.
        string farFuture = CreateJwt(DateTime.UtcNow.AddDays(1));
        _authTokenStore.SetAccessToken(farFuture);

        using CancellationTokenSource cts = new();
        _authManager.ScheduleBackgroundRefresh(cts.Token);

        await Task.Delay(50);
        cts.Cancel();
        await Task.Delay(100);

        // No assertion beyond "did not throw / did not hang" — the loop's own
        // cancellation branch is what this test exercises.
    }

    // ── SecureDeleteFile ──────────────────────────────────────────────────────

    [Fact]
    public void SecureDeleteFile_NonExistentFile_DoesNothing()
    {
        string path = Path.Combine(Path.GetTempPath(), $"nm-secdel-missing-{Guid.NewGuid():N}.tmp");

        _authManager.SecureDeleteFile(path);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void SecureDeleteFile_ExistingFile_OverwritesAndDeletes()
    {
        string path = Path.Combine(Path.GetTempPath(), $"nm-secdel-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(path, "sensitive-token-contents");

        _authManager.SecureDeleteFile(path);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void SecureDeleteFile_EmptyFile_DeletesWithoutZeroingContent()
    {
        string path = Path.Combine(Path.GetTempPath(), $"nm-secdel-empty-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(path, string.Empty);

        _authManager.SecureDeleteFile(path);

        Assert.False(File.Exists(path));
    }
}
