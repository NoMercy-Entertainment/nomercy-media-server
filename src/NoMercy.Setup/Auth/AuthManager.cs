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

using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using NoMercy.Database;
using NoMercy.Database.Models.Common;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Setup.Dto;
using NoMercy.Storage;
using Serilog.Events;

namespace NoMercy.Setup.Auth;

public class AuthManager
{
    private readonly AppDbContext _appContext;

    // Serialises ALL _appContext access: it is a non-thread-safe EF DbContext, so
    // a read (LoadSecureValue) racing a write (UpsertSecureValue) — e.g. the boot
    // refresh timer reading while a PKCE callback stores tokens — throws "a second
    // operation was started on this DbContext". Every access below takes this.
    private readonly SemaphoreSlim _upsertLock = new(1, 1);
    private readonly IStorageDriver _driver;

    private readonly object _authReadyLock = new();
    private TaskCompletionSource _authReadyTcs = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private CancellationTokenSource? _refreshCts;

    private static readonly TimeSpan TransientRefreshRetryDelay = TimeSpan.FromSeconds(15);

    private readonly IAuthTokenStore _authTokenStore;

    public AuthManager(
        AppDbContext appContext,
        IStorageDriver driver,
        IAuthTokenStore authTokenStore
    )
    {
        _authTokenStore = authTokenStore;
        _appContext = appContext;
        _driver = driver;
    }

    // ── Public API ───────────────────────────────────────────────────────────

    public Task WaitForAuthReadyAsync(CancellationToken ct)
    {
        lock (_authReadyLock)
        {
            return _authReadyTcs.Task.WaitAsync(ct);
        }
    }

    public async Task<bool> InitializeAsync()
    {
        MigrateLegacyTokenFile();

        string? accessToken = await LoadSecureValue("auth_access_token");
        string? refreshToken = await LoadSecureValue("auth_refresh_token");
        string? metadataJson = await LoadSecureValue("auth_token_metadata");

        if (string.IsNullOrEmpty(accessToken))
        {
            Logger.Auth("No cached token in DB — authentication required through /setup UI");
            return false;
        }

        if (!TokenIssuerMatchesConfiguredRealm(accessToken))
        {
            Logger.Auth(
                $"Cached token issuer doesn't match configured realm {ExternalServicesConfig.Current.AuthBaseUrl} — discarding and requiring re-auth",
                LogEventLevel.Warning
            );
            await UpsertSecureValue("auth_access_token", string.Empty);
            await UpsertSecureValue("auth_refresh_token", string.Empty);
            await UpsertSecureValue("auth_token_metadata", string.Empty);
            return false;
        }

        DateTime expiresAt = ParseExpiresAt(accessToken, metadataJson);
        bool isValid = expiresAt > DateTime.UtcNow.AddMinutes(5);

        if (isValid)
        {
            _authTokenStore.SetAccessToken(accessToken);
            OfflineJwksCache.LoadCachedPublicKey();
            SignalAuthReady();
            Logger.Auth("Using cached token (still valid)");
            return true;
        }

        if (!string.IsNullOrEmpty(refreshToken))
        {
            Logger.Auth("Token expired — attempting refresh with retries");
            int[] delays = [1, 3, 5]; // seconds — a brief Keycloak hiccup at boot
            // must not force a full re-auth when the refresh token is still valid.
            for (int attempt = 0; attempt < delays.Length; attempt++)
            {
                bool refreshed = await TryRefreshToken(refreshToken);
                if (refreshed)
                {
                    SignalAuthReady();
                    return true;
                }

                Logger.Auth(
                    $"Refresh attempt {attempt + 1} failed, waiting {delays[attempt]}s before retry..."
                );
                await Task.Delay(TimeSpan.FromSeconds(delays[attempt]));
            }
        }

        Logger.Auth(
            "Token expired and refresh failed — authentication required",
            LogEventLevel.Warning
        );
        return false;
    }

    public async Task StoreTokensAsync(
        string accessToken,
        string? refreshToken,
        DateTime expiresAt,
        string tokenType
    )
    {
        await UpsertSecureValue("auth_access_token", accessToken);

        if (!string.IsNullOrEmpty(refreshToken))
            await UpsertSecureValue("auth_refresh_token", refreshToken);

        TokenMetadata metadata = new()
        {
            ExpiresAt = expiresAt.ToString("O"),
            TokenType = tokenType,
        };
        await UpsertSecureValue("auth_token_metadata", JsonConvert.SerializeObject(metadata));

        _authTokenStore.SetAccessToken(accessToken);
        SignalAuthReady();

        Logger.Auth("Tokens stored to DB");
    }

    public async Task StoreTokensAsync(AuthResponse tokens)
    {
        string? accessToken = tokens.AccessToken;
        if (string.IsNullOrEmpty(accessToken))
        {
            Logger.Auth("StoreTokensAsync called with null access token", LogEventLevel.Warning);
            return;
        }

        DateTime expiresAt;
        try
        {
            JwtSecurityTokenHandler handler = new();
            JwtSecurityToken jwt = handler.ReadJwtToken(accessToken);
            expiresAt = jwt.ValidTo;
        }
        catch
        {
            expiresAt = DateTime.UtcNow.AddSeconds(tokens.ExpiresIn > 0 ? tokens.ExpiresIn : 300);
        }

        await StoreTokensAsync(
            accessToken,
            tokens.RefreshToken,
            expiresAt,
            tokens.TokenType ?? "Bearer"
        );
    }

    public async Task RefreshAsync()
    {
        string? refreshToken = await LoadSecureValue("auth_refresh_token");

        if (string.IsNullOrEmpty(refreshToken))
        {
            Logger.Auth("No refresh token in DB — re-auth required", LogEventLevel.Warning);
            _authTokenStore.SetAccessToken(null);
            ResetAuthReady();
            return;
        }

        bool success = await TryRefreshToken(refreshToken);
        if (success)
            return;

        // A dead refresh token already cleared the store, so this is a transient failure.
        if (IsUnexpiredAccessToken(_authTokenStore.AccessToken))
        {
            Logger.Auth(
                "Background refresh failed — keeping the unexpired access token and retrying",
                LogEventLevel.Warning
            );
            return;
        }

        Logger.Auth("Background refresh failed — clearing access token", LogEventLevel.Warning);
        _authTokenStore.SetAccessToken(null);
        ResetAuthReady();
    }

    private static bool IsUnexpiredAccessToken(string? accessToken)
    {
        if (string.IsNullOrEmpty(accessToken))
            return false;

        try
        {
            JwtSecurityTokenHandler handler = new();
            return handler.ReadJwtToken(accessToken).ValidTo > DateTime.UtcNow;
        }
        catch
        {
            return false;
        }
    }

    private async Task HandleDeadRefreshTokenAsync()
    {
        _refreshCts?.Cancel();
        await UpsertSecureValue("auth_refresh_token", string.Empty);
        _authTokenStore.SetAccessToken(null);
        ResetAuthReady();
        Logger.Auth(
            "Refresh token rejected as invalid_grant — re-authentication required through /setup UI",
            LogEventLevel.Warning
        );
    }

    public void ScheduleBackgroundRefresh(CancellationToken ct)
    {
        _refreshCts?.Cancel();
        _refreshCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationToken linked = _refreshCts.Token;

        _ = Task.Run(
            async () =>
            {
                while (!linked.IsCancellationRequested)
                {
                    try
                    {
                        string? accessToken = _authTokenStore.AccessToken;
                        DateTime expiry = DateTime.UtcNow.AddMinutes(5);

                        if (!string.IsNullOrEmpty(accessToken))
                        {
                            try
                            {
                                JwtSecurityTokenHandler handler = new();
                                JwtSecurityToken jwt = handler.ReadJwtToken(accessToken);
                                expiry = jwt.ValidTo;
                            }
                            catch
                            {
                                // Fallback to near-immediate refresh
                            }
                        }

                        TimeSpan delay = expiry - DateTime.UtcNow - TimeSpan.FromSeconds(60);
                        if (delay > TimeSpan.Zero)
                            await Task.Delay(delay, linked);

                        if (linked.IsCancellationRequested)
                            break;

                        Logger.Auth("Proactive token refresh", LogEventLevel.Verbose);
                        await RefreshAsync();

                        // A kept token is still inside the refresh window: pace the retry.
                        if (
                            !string.IsNullOrEmpty(accessToken)
                            && _authTokenStore.AccessToken == accessToken
                        )
                            await Task.Delay(TransientRefreshRetryDelay, linked);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.Auth(
                            $"Background refresh error: {ex.Unwrap()} — retrying in 60s",
                            LogEventLevel.Warning
                        );
                        await Task.Delay(TimeSpan.FromSeconds(60), linked);
                    }
                }
            },
            linked
        );
    }

    // ── Standalone browser PKCE state (for desktop app flow) ────────────────
    private static string? _pendingCodeVerifier;
    private static string? _pendingState;
    private static TaskCompletionSource<bool>? _pkceCompletionSource;

    public static void PreparePkceBrowserFlow(string codeVerifier, string state)
    {
        _pendingCodeVerifier = codeVerifier;
        _pendingState = state;
        _pkceCompletionSource = new();
    }

    public static Task<bool>? GetPkceBrowserTask() => _pkceCompletionSource?.Task;

    public static async Task<bool> TryCompletePkceFromCallbackAsync(
        string code,
        string state,
        string redirectUri,
        IAuthTokenStore? authTokenStore = null
    )
    {
        if (_pendingCodeVerifier is null || _pendingState is null || _pkceCompletionSource is null)
            return false;

        if (state != _pendingState)
            return false;

        try
        {
            if (string.IsNullOrEmpty(ExternalServicesConfig.Current.TokenClientId))
                throw new InvalidOperationException("Auth configuration not available");

            List<KeyValuePair<string, string>> body = BuildAuthorizationCodeBody(
                ExternalServicesConfig.Current.TokenClientId,
                code,
                redirectUri,
                _pendingCodeVerifier
            );

            string tokenEndpoint =
                $"{ExternalServicesConfig.Current.AuthBaseUrl}protocol/openid-connect/token";

            using HttpClient httpClient = new();
            httpClient.WithNoMercyUserAgent();

            using HttpResponseMessage response = await httpClient.PostAsync(
                tokenEndpoint,
                new FormUrlEncodedContent(body)
            );

            string content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Token exchange failed ({(int)response.StatusCode}): {content}"
                );

            AuthResponse? data = JsonConvert.DeserializeObject<AuthResponse>(content);
            if (data?.AccessToken is null)
                throw new InvalidOperationException("Token response missing access_token");

            authTokenStore?.SetAccessToken(data.AccessToken);
            _pkceCompletionSource.TrySetResult(true);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Auth(
                $"PKCE callback failed: {ex.DescribeConnectionFailure()}",
                LogEventLevel.Error
            );
            _pkceCompletionSource?.TrySetException(ex);
            return false;
        }
        finally
        {
            _pendingCodeVerifier = null;
            _pendingState = null;
            _pkceCompletionSource = null;
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private void SignalAuthReady()
    {
        lock (_authReadyLock)
        {
            _authReadyTcs.TrySetResult();
        }
    }

    private void ResetAuthReady()
    {
        lock (_authReadyLock)
        {
            if (_authReadyTcs.Task.IsCompleted)
                _authReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private async Task<bool> TryRefreshToken(string refreshToken)
    {
        if (string.IsNullOrEmpty(ExternalServicesConfig.Current.TokenClientId))
        {
            Logger.Auth("TokenClientId not configured — cannot refresh", LogEventLevel.Warning);
            return false;
        }

        try
        {
            string tokenEndpoint =
                $"{ExternalServicesConfig.Current.AuthBaseUrl}protocol/openid-connect/token";

            List<KeyValuePair<string, string>> body = BuildRefreshTokenBody(
                ExternalServicesConfig.Current.TokenClientId,
                refreshToken
            );

            using HttpClient httpClient = new();
            httpClient.WithNoMercyUserAgent();

            using HttpResponseMessage response = await httpClient.PostAsync(
                tokenEndpoint,
                new FormUrlEncodedContent(body)
            );

            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync();
                Logger.Auth(
                    $"Token refresh returned {(int)response.StatusCode}: {errorBody}",
                    LogEventLevel.Warning
                );

                if (IsPermanentRefreshFailure(errorBody))
                    await HandleDeadRefreshTokenAsync();

                return false;
            }

            string content = await response.Content.ReadAsStringAsync();
            AuthResponse? data = JsonConvert.DeserializeObject<AuthResponse>(content);

            if (data?.AccessToken == null)
            {
                Logger.Auth("Token refresh response missing access_token", LogEventLevel.Warning);
                return false;
            }

            await StoreTokensAsync(data);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Auth($"Token refresh exception: {ex.Unwrap()}", LogEventLevel.Warning);
            return false;
        }
    }

    private static bool TokenIssuerMatchesConfiguredRealm(string accessToken)
    {
        try
        {
            JwtSecurityTokenHandler handler = new();
            JwtSecurityToken jwt = handler.ReadJwtToken(accessToken);
            string issuer = (jwt.Issuer ?? string.Empty).TrimEnd('/');
            string configured = ExternalServicesConfig.Current.AuthBaseUrl.TrimEnd('/');
            return issuer.Equals(configured, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void MigrateLegacyTokenFile()
    {
#pragma warning disable CS0618
        string tokenFilePath = AppFiles.TokenFile;
#pragma warning restore CS0618

        if (!_driver.FileExists(tokenFilePath))
            return;

        try
        {
            string fileContents;
            using (StreamReader reader = new(_driver.OpenRead(tokenFilePath)))
                fileContents = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(fileContents) || fileContents.Trim() == "{}")
            {
                SecureDeleteFile(tokenFilePath);
                return;
            }

            AuthResponse? tokenData = JsonConvert.DeserializeObject<AuthResponse>(fileContents);
            if (tokenData?.AccessToken == null)
            {
                SecureDeleteFile(tokenFilePath);
                return;
            }

            // Store synchronously via blocking call during migration
            StoreTokensAsync(tokenData).GetAwaiter().GetResult();
            SecureDeleteFile(tokenFilePath);
            Logger.Auth("Migrated legacy token.json to encrypted DB storage");
        }
        catch (Exception ex)
        {
            Logger.Auth(
                $"Legacy token migration failed: {ex.Message} — file left intact",
                LogEventLevel.Warning
            );
        }
    }

    private async Task<string?> LoadSecureValue(string key)
    {
        await _upsertLock.WaitAsync();
        try
        {
            Configuration? row = await _appContext
                .Configuration.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Key == key);

            return row?.SecureValue;
        }
        finally
        {
            _upsertLock.Release();
        }
    }

    private async Task UpsertSecureValue(string key, string value)
    {
        await _upsertLock.WaitAsync();
        try
        {
            Configuration? existing = await _appContext.Configuration.FirstOrDefaultAsync(c =>
                c.Key == key
            );

            if (existing is not null)
            {
                existing.SecureValue = value;
                _appContext.Configuration.Update(existing);
            }
            else
            {
                _appContext.Configuration.Add(
                    new()
                    {
                        Key = key,
                        Value = string.Empty,
                        SecureValue = value,
                    }
                );
            }

            await _appContext.SaveChangesAsync();
        }
        finally
        {
            _upsertLock.Release();
        }
    }

    private static DateTime ParseExpiresAt(string? accessToken, string? metadataJson)
    {
        // Try metadata first
        if (!string.IsNullOrEmpty(metadataJson))
        {
            try
            {
                TokenMetadata? metadata = JsonConvert.DeserializeObject<TokenMetadata>(
                    metadataJson
                );
                if (
                    metadata?.ExpiresAt is not null
                    && DateTime.TryParse(
                        metadata.ExpiresAt,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind,
                        out DateTime parsedExpiry
                    )
                )
                    return parsedExpiry;
            }
            catch
            {
                // Fall through to JWT
            }
        }

        // Fall back to JWT exp claim
        if (!string.IsNullOrEmpty(accessToken))
        {
            try
            {
                JwtSecurityTokenHandler handler = new();
                JwtSecurityToken jwt = handler.ReadJwtToken(accessToken);
                return jwt.ValidTo;
            }
            catch
            {
                // Fall through to epoch
            }
        }

        return DateTime.MinValue;
    }

    // ── Static PKCE helpers (copied from Auth.cs) ────────────────────────────

    public static string GenerateCodeVerifier()
    {
        byte[] bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string GenerateCodeChallenge(string codeVerifier)
    {
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static List<KeyValuePair<string, string>> BuildAuthorizationCodeBody(
        string clientId,
        string code,
        string redirectUri,
        string codeVerifier
    )
    {
        return
        [
            new("grant_type", "authorization_code"),
            new("client_id", clientId),
            new("scope", "openid offline_access email profile"),
            new("redirect_uri", redirectUri),
            new("code", code),
            new("code_verifier", codeVerifier),
        ];
    }

    public static bool IsPermanentRefreshFailure(string errorBody) =>
        errorBody.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase);

    public static List<KeyValuePair<string, string>> BuildRefreshTokenBody(
        string clientId,
        string refreshToken
    )
    {
        return
        [
            new("grant_type", "refresh_token"),
            new("client_id", clientId),
            new("refresh_token", refreshToken),
            new("scope", "openid offline_access email profile"),
        ];
    }

    public static List<KeyValuePair<string, string>> BuildDeviceCodeRequestBody(string clientId)
    {
        return [new("client_id", clientId), new("scope", "openid offline_access email profile")];
    }

    public static List<KeyValuePair<string, string>> BuildDeviceTokenBody(
        string clientId,
        string deviceCode
    )
    {
        return
        [
            new("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
            new("client_id", clientId),
            new("device_code", deviceCode),
        ];
    }

    public void SecureDeleteFile(string path)
    {
        try
        {
            if (!_driver.FileExists(path))
                return;

            long fileLength = _driver.GetFileSize(path);
            if (fileLength > 0)
            {
                using Stream stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.None
                );
                byte[] zeros = new byte[Math.Min(fileLength, 4096)];
                long remaining = fileLength;
                while (remaining > 0)
                {
                    int chunk = (int)Math.Min(remaining, zeros.Length);
                    stream.Write(zeros, 0, chunk);
                    remaining -= chunk;
                }
                stream.Flush();
            }

            _driver.DeleteFile(path);
        }
        catch (Exception ex)
        {
            Logger.Auth($"SecureDeleteFile failed for {path}: {ex.Message}", LogEventLevel.Warning);
        }
    }

    public static bool IsDesktopEnvironment()
    {
        if (
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
        )
            return true;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return false;

        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
    }

    public static void OpenBrowser(string url)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            Process.Start("xdg-open", url).Dispose();
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Process.Start("open", url).Dispose();
        else
            throw new PlatformNotSupportedException("Unsupported OS for browser launch");
    }

    // ── Inner types ──────────────────────────────────────────────────────────

    private sealed class TokenMetadata
    {
        [JsonProperty("expires_at")]
        public string? ExpiresAt { get; set; }

        [JsonProperty("token_type")]
        public string? TokenType { get; set; }
    }
}
