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

using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using Newtonsoft.Json;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Setup.Dto;
using Serilog.Events;

namespace NoMercy.Setup.Cast;

/// <summary>
/// Mints LaunchCustomData bundles for cast sessions. Calls Keycloak's
/// token-exchange grant against the nomercy-cast-receiver client with the
/// CALLER's own validated bearer as the subject, so the receiver gets a token
/// that belongs to the user who casts. The server's own token is never the subject.
///
/// Access token only: no refresh token ever leaves the server for a receiver.
/// Any failure returns null so the receiver keeps the sender-token path.
/// </summary>
public class CastSessionTokenService
{
    private const string CastReceiverClientId = "nomercy-cast-receiver";
    private const string TokenExchangeGrantType = "urn:ietf:params:oauth:grant-type:token-exchange";
    private const string AccessTokenType = "urn:ietf:params:oauth:token-type:access_token";
    internal const int MaxMintsPerWindow = 10;
    internal static readonly TimeSpan MintWindow = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<Guid, Queue<DateTime>> _recentMints = new();

    internal Action<string> MintLog { get; set; } = line => Logger.Auth(line);

    public async Task<LaunchCustomData?> MintAsync(
        string? callerBearer,
        Guid userId,
        string serverId,
        string serverUrl,
        Ulid deviceId,
        CastIntent intent,
        string clientLocale = "en-US"
    )
    {
        if (string.IsNullOrWhiteSpace(callerBearer))
        {
            Logger.Auth(
                "CastSessionTokenService: no caller bearer at hand — not minting a cast bundle",
                LogEventLevel.Debug
            );
            return null;
        }

        JwtSecurityToken? caller = ReadCaller(callerBearer);
        string? azp = caller is null ? null : ClaimOf(caller, "azp");
        if (
            caller is null
            || string.IsNullOrEmpty(azp)
            || !Guid.TryParse(caller.Subject, out Guid subject)
            || subject != userId
        )
        {
            Logger.Auth(
                "CastSessionTokenService: caller bearer has no azp or does not belong to the user — not minting",
                LogEventLevel.Warning
            );
            return null;
        }

        if (!IssuerMatchesConfiguredRealm(caller))
        {
            Logger.Auth(
                $"CastSessionTokenService: caller token issuer doesn't match configured realm {ExternalServicesConfig.Current.AuthBaseUrl} — not minting",
                LogEventLevel.Warning
            );
            return null;
        }

        if (!TryTakeMintSlot(userId))
        {
            Logger.Auth(
                $"CastSessionTokenService: mint rate limit reached for sub={userId} — not minting",
                LogEventLevel.Warning
            );
            return null;
        }

        AuthResponse? exchanged = await RequestTokenExchangeAsync(callerBearer, azp, caller);
        if (string.IsNullOrEmpty(exchanged?.AccessToken))
        {
            Logger.Auth(
                "CastSessionTokenService: token-exchange returned no usable token",
                LogEventLevel.Warning
            );
            return null;
        }

        string castSessionId = Guid.NewGuid().ToString();
        MintLog(
            $"Cast session minted sub={userId} azp={azp} device_id={deviceId} cast_session_id={castSessionId}"
        );

        return new()
        {
            AccessToken = exchanged.AccessToken,
            RefreshToken = null,
            UserId = userId.ToString(),
            ServerId = serverId,
            ServerUrl = serverUrl,
            DeviceId = deviceId.ToString(),
            Intent = intent,
            CastSessionId = castSessionId,
            LaunchTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ClientLocale = clientLocale,
        };
    }

    private bool TryTakeMintSlot(Guid userId)
    {
        Queue<DateTime> mints = _recentMints.GetOrAdd(userId, _ => new());
        lock (mints)
        {
            DateTime now = DateTime.UtcNow;
            while (mints.Count > 0 && now - mints.Peek() > MintWindow)
                mints.Dequeue();
            if (mints.Count >= MaxMintsPerWindow)
                return false;
            mints.Enqueue(now);
            return true;
        }
    }

    private static async Task<AuthResponse?> RequestTokenExchangeAsync(
        string callerBearer,
        string requestingClientId,
        JwtSecurityToken caller
    )
    {
        string tokenEndpoint =
            $"{ExternalServicesConfig.Current.AuthBaseUrl}protocol/openid-connect/token";

        List<KeyValuePair<string, string>> body =
        [
            new("grant_type", TokenExchangeGrantType),
            new("client_id", requestingClientId),
            new("subject_token", callerBearer),
            new("subject_token_type", AccessTokenType),
            new("audience", CastReceiverClientId),
            new("requested_token_type", AccessTokenType),
            new("scope", "openid"),
        ];

        try
        {
            using HttpClient httpClient = new();
            httpClient.WithNoMercyUserAgent();

            using HttpResponseMessage response = await httpClient.PostAsync(
                tokenEndpoint,
                new FormUrlEncodedContent(body)
            );

            string content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Logger.Auth(
                    $"Cast token-exchange failed ({(int)response.StatusCode}) | endpoint: {tokenEndpoint} | subject: {DescribeSubjectToken(caller)} | requesting_client: {requestingClientId} target_audience: {CastReceiverClientId}",
                    LogEventLevel.Warning
                );
                return null;
            }

            return JsonConvert.DeserializeObject<AuthResponse>(content);
        }
        catch (Exception ex)
        {
            Logger.Auth($"Cast token-exchange exception: {ex.Message}", LogEventLevel.Warning);
            return null;
        }
    }

    private static JwtSecurityToken? ReadCaller(string bearer)
    {
        try
        {
            return new JwtSecurityTokenHandler().ReadJwtToken(bearer);
        }
        catch
        {
            return null;
        }
    }

    private static string? ClaimOf(JwtSecurityToken jwt, string name) =>
        jwt.Payload.TryGetValue(name, out object? value) && value is string s ? s : null;

    private static bool IssuerMatchesConfiguredRealm(JwtSecurityToken jwt)
    {
        string issuer = jwt.Issuer ?? string.Empty;
        string configured = ExternalServicesConfig.Current.AuthBaseUrl.TrimEnd('/');
        return issuer.TrimEnd('/').Equals(configured, StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeSubjectToken(JwtSecurityToken jwt)
    {
        string issuer = jwt.Issuer ?? "?";
        string azp = ClaimOf(jwt, "azp") ?? "?";
        string aud = string.Join(",", jwt.Audiences);
        string sub = jwt.Subject ?? "?";
        DateTime exp = jwt.ValidTo;
        TimeSpan remaining = exp - DateTime.UtcNow;
        return $"iss={issuer} azp={azp} aud=[{aud}] sub={sub} exp={exp:O} remaining={remaining.TotalSeconds:F0}s";
    }
}
