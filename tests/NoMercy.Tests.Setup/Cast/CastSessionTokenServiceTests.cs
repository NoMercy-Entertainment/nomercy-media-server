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
using Newtonsoft.Json;
using NoMercy.NmSystem.Auth;
using NoMercy.Setup.Cast;
using NoMercy.Setup.Dto;
using NoMercy.Tests.Setup.Infrastructure;

namespace NoMercy.Tests.Setup.Cast;

/// <summary>
/// A cast session token is minted for the user who casts: the exchange subject is the
/// caller's bearer, failures mint nothing, mints are rate limited, logs hold no token.
/// </summary>
[Trait("Category", "Unit")]
public sealed class CastSessionTokenServiceTests
{
    private static string CreateJwt(
        string issuer,
        Guid sub,
        string? azp = "nomercy-ui",
        string marker = "caller"
    )
    {
        List<System.Security.Claims.Claim> claims =
        [
            new("sub", sub.ToString()),
            new("marker", marker),
        ];
        if (azp is not null)
            claims.Add(new("azp", azp));

        JwtSecurityToken token = new(
            issuer: issuer,
            audience: "nomercy-server",
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-5),
            expires: DateTime.UtcNow.AddHours(1)
        );
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string ExchangeJson(string? refreshToken = null) =>
        JsonConvert.SerializeObject(
            new AuthResponse
            {
                AccessToken = "exchanged-access-token",
                RefreshToken = refreshToken,
                TokenType = "Bearer",
                ExpiresIn = 3600,
            }
        );

    private static Task<LaunchCustomData?> Mint(
        CastSessionTokenService service,
        string? bearer,
        Guid userId,
        Ulid? deviceId = null
    ) =>
        service.MintAsync(
            bearer,
            userId,
            "server-1",
            "https://server1.nomercy.app",
            deviceId ?? Ulid.NewUlid(),
            CastIntent.Idle()
        );

    [Fact]
    public async Task MintAsync_NoCallerBearer_ReturnsNull()
    {
        Assert.Null(await Mint(new(), null, Guid.NewGuid()));
        Assert.Null(await Mint(new(), "  ", Guid.NewGuid()));
    }

    [Fact]
    public async Task MintAsync_ExchangeBody_SubjectIsTheCallersTokenAndNeverTheServers()
    {
        using LoopbackHttpServer server = new();
        Guid callerId = Guid.NewGuid();
        string callerJwt = CreateJwt(server.BaseUrl, callerId, azp: "nomercy-ui");
        string serverJwt = CreateJwt(server.BaseUrl, Guid.NewGuid(), "nomercy-server", "server");
        new AuthTokenStore().SetAccessToken(serverJwt);

        string? body = null;
        server.Handler = req =>
        {
            body = req.Body;
            return new(200, ExchangeJson());
        };
        using ExternalServicesConfigScope scope = new(authBaseUrl: server.BaseUrl);

        LaunchCustomData? result = await Mint(new(), callerJwt, callerId);

        Assert.NotNull(result);
        Assert.NotNull(body);
        Dictionary<string, string> form = ParseForm(body);
        Assert.Equal(callerJwt, form["subject_token"]);
        Assert.DoesNotContain(serverJwt, body);
        Assert.Equal("nomercy-ui", form["client_id"]);
        Assert.Equal("urn:ietf:params:oauth:token-type:access_token", form["requested_token_type"]);
        Assert.Equal("nomercy-cast-receiver", form["audience"]);
    }

    [Fact]
    public async Task MintAsync_Success_CarriesAnAccessTokenAndNoRefreshToken()
    {
        using LoopbackHttpServer server = new();
        Guid callerId = Guid.NewGuid();
        server.Handler = _ => new(200, ExchangeJson(refreshToken: "must-not-leak"));
        using ExternalServicesConfigScope scope = new(authBaseUrl: server.BaseUrl);
        Ulid deviceId = Ulid.NewUlid();

        LaunchCustomData? result = await Mint(
            new(),
            CreateJwt(server.BaseUrl, callerId),
            callerId,
            deviceId
        );

        Assert.NotNull(result);
        Assert.Equal("exchanged-access-token", result.AccessToken);
        Assert.Null(result.RefreshToken);
        Assert.Equal(callerId.ToString(), result.UserId);
        Assert.Equal(deviceId.ToString(), result.DeviceId);
        Assert.NotEmpty(result.CastSessionId);
    }

    [Fact]
    public async Task MintAsync_ExchangeFailsOrAborts_MintsNothing()
    {
        using LoopbackHttpServer server = new();
        Guid callerId = Guid.NewGuid();
        string jwt = CreateJwt(server.BaseUrl, callerId);
        using ExternalServicesConfigScope scope = new(authBaseUrl: server.BaseUrl);

        server.Handler = _ => new(403, "{\"error\":\"not_allowed\"}");
        Assert.Null(await Mint(new(), jwt, callerId));

        server.Handler = _ => LoopbackResponse.Aborted();
        Assert.Null(await Mint(new(), jwt, callerId));

        server.Handler = _ => new(200, JsonConvert.SerializeObject(new AuthResponse()));
        Assert.Null(await Mint(new(), jwt, callerId));
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("azp")]
    [InlineData("sub")]
    [InlineData("garbage")]
    public async Task MintAsync_UnusableCallerToken_NeverReachesTheExchange(string defect)
    {
        using LoopbackHttpServer server = new();
        Guid callerId = Guid.NewGuid();
        int exchanges = 0;
        server.Handler = _ =>
        {
            exchanges++;
            return new(200, ExchangeJson());
        };
        using ExternalServicesConfigScope scope = new(authBaseUrl: server.BaseUrl);

        string bearer = defect switch
        {
            "issuer" => CreateJwt("https://auth.example.com/realms/Other", callerId),
            "azp" => CreateJwt(server.BaseUrl, callerId, azp: null),
            "sub" => CreateJwt(server.BaseUrl, Guid.NewGuid()),
            _ => "not-a-jwt-at-all",
        };

        Assert.Null(await Mint(new(), bearer, callerId));
        Assert.Equal(0, exchanges);
    }

    [Fact]
    public async Task MintAsync_BurstFromOneUser_IsRefusedAfterTheLimit()
    {
        using LoopbackHttpServer server = new();
        Guid callerId = Guid.NewGuid();
        int exchanges = 0;
        server.Handler = _ =>
        {
            Interlocked.Increment(ref exchanges);
            return new(200, ExchangeJson());
        };
        using ExternalServicesConfigScope scope = new(authBaseUrl: server.BaseUrl);
        string jwt = CreateJwt(server.BaseUrl, callerId);
        CastSessionTokenService service = new();

        for (int i = 0; i < CastSessionTokenService.MaxMintsPerWindow; i++)
            Assert.NotNull(await Mint(service, jwt, callerId));

        Assert.Null(await Mint(service, jwt, callerId));
        Assert.Equal(CastSessionTokenService.MaxMintsPerWindow, exchanges);

        Guid other = Guid.NewGuid();
        Assert.NotNull(await Mint(service, CreateJwt(server.BaseUrl, other), other));
    }

    [Fact]
    public async Task MintAsync_LogLine_NamesTheSessionAndHoldsNoTokenText()
    {
        using LoopbackHttpServer server = new();
        Guid callerId = Guid.NewGuid();
        string jwt = CreateJwt(server.BaseUrl, callerId, azp: "nomercy-ui");
        server.Handler = _ => new(200, ExchangeJson(refreshToken: "refresh-secret"));
        using ExternalServicesConfigScope scope = new(authBaseUrl: server.BaseUrl);
        List<string> lines = [];
        CastSessionTokenService service = new() { MintLog = lines.Add };
        Ulid deviceId = Ulid.NewUlid();

        LaunchCustomData? result = await Mint(service, jwt, callerId, deviceId);

        Assert.NotNull(result);
        string line = Assert.Single(lines);
        Assert.Contains($"sub={callerId}", line);
        Assert.Contains("azp=nomercy-ui", line);
        Assert.Contains($"device_id={deviceId}", line);
        Assert.Contains($"cast_session_id={result.CastSessionId}", line);
        Assert.DoesNotContain(jwt, line);
        Assert.DoesNotContain("exchanged-access-token", line);
        Assert.DoesNotContain("refresh-secret", line);
        Assert.DoesNotContain("eyJ", line);
    }

    private static Dictionary<string, string> ParseForm(string body) =>
        body.Split('&')
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
}
