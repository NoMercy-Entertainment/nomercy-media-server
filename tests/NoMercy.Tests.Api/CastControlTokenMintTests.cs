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
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NoMercy.Api.Services.Cast;
using NoMercy.Database;
using NoMercy.Database.Models.Users;
using NoMercy.NmSystem.Information;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api;

/// <summary>
/// A phone asks the server for a short token scoped to one TV, so it stops sending its
/// full Keycloak token to a LAN host. The TV checks the token against the server's
/// published key and its own fingerprint.
/// </summary>
[Trait("Category", "Integration")]
public class CastControlTokenMintTests : IClassFixture<NoMercyApiFactory>
{
    private readonly NoMercyApiFactory _factory;

    public CastControlTokenMintTests(NoMercyApiFactory factory)
    {
        _factory = factory;
    }

    private static string Url(string deviceId) => $"/api/v1/cast/{deviceId}/token";

    private static async Task<string> SeedTvAsync(Guid? owner, string? fingerprint)
    {
        string deviceId = "cast-token-" + Guid.NewGuid().ToString("N");
        await using MediaContext db = new();
        db.Devices.Add(
            new()
            {
                DeviceId = deviceId,
                Type = "tv",
                Name = "Placeholder TV",
                OwnerUserId = owner,
                Fingerprint = fingerprint,
                LanIp = "203.0.113.10",
            }
        );
        await db.SaveChangesAsync();
        return deviceId;
    }

    private static JsonElement Part(string jws, int index)
    {
        string[] parts = jws.Split('.');
        Assert.Equal(3, parts.Length);
        return JsonDocument.Parse(Base64Url(parts[index])).RootElement;
    }

    private static byte[] Base64Url(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        return Convert.FromBase64String(padded);
    }

    private static bool Verifies(string jws, JsonElement jwk)
    {
        string[] parts = jws.Split('.');
        using ECDsa key = ECDsa.Create(
            new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new()
                {
                    X = Base64Url(jwk.GetProperty("x").GetString()!),
                    Y = Base64Url(jwk.GetProperty("y").GetString()!),
                },
            }
        );
        return key.VerifyData(
            Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
            Base64Url(parts[2]),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation
        );
    }

    private JsonElement PublishedKey() =>
        JsonDocument
            .Parse(_factory.Services.GetRequiredService<CastControlTokenService>().PublicJwksJson())
            .RootElement.GetProperty("keys")[0];

    private async Task<string> MintAsync(string deviceId)
    {
        HttpClient owner = _factory.CreateClient().AsAuthenticated();
        HttpResponseMessage response = await owner.PostAsync(Url(deviceId), null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement;
        Assert.Equal(deviceId, body.GetProperty("device_id").GetString());
        return body.GetProperty("token").GetString()!;
    }

    [Fact]
    public async Task Owner_MintsAToken_ThatVerifiesAgainstThePublishedKey_WithTheListedClaims()
    {
        string fp = "placeholder-fp-" + Guid.NewGuid().ToString("N");
        string deviceId = await SeedTvAsync(TestAuthHandler.DefaultUserId, fp);

        HttpResponseMessage response = await _factory
            .CreateClient()
            .AsAuthenticated()
            .PostAsync(Url(deviceId), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement;
        string token = body.GetProperty("token").GetString()!;
        JsonElement jwk = PublishedKey();

        Assert.True(Verifies(token, jwk));
        JsonElement header = Part(token, 0);
        Assert.Equal("ES256", header.GetProperty("alg").GetString());
        Assert.Equal(jwk.GetProperty("kid").GetString(), header.GetProperty("kid").GetString());

        JsonElement claims = Part(token, 1);
        Assert.Equal(Info.DeviceId.ToString(), claims.GetProperty("iss").GetString());
        Assert.Equal(
            TestAuthHandler.DefaultUserId.ToString(),
            claims.GetProperty("sub").GetString()
        );
        Assert.Equal("tv-control", claims.GetProperty("aud").GetString());
        Assert.Equal("tv-control", claims.GetProperty("scope").GetString());
        Assert.Equal(deviceId, claims.GetProperty("device_id").GetString());
        Assert.Equal(fp, claims.GetProperty("fp").GetString());
        Assert.Equal(
            600,
            claims.GetProperty("exp").GetInt64() - claims.GetProperty("iat").GetInt64()
        );
        Assert.False(string.IsNullOrEmpty(claims.GetProperty("jti").GetString()));

        DateTimeOffset expiresAt = DateTimeOffset.Parse(
            body.GetProperty("expires_at").GetString()!
        );
        Assert.Equal(claims.GetProperty("exp").GetInt64(), expiresAt.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task UserWhoDoesNotOwnTheDevice_IsForbidden()
    {
        string deviceId = await SeedTvAsync(TestAuthHandler.DefaultUserId, "placeholder-fp-a");

        HttpResponseMessage response = await _factory
            .CreateClient()
            .AsSecondaryUser()
            .PostAsync(Url(deviceId), null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UnknownDevice_IsNotFound()
    {
        HttpResponseMessage response = await _factory
            .CreateClient()
            .AsAuthenticated()
            .PostAsync(Url("cast-token-unknown"), null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task DeviceWithoutAFingerprint_IsAConflict_WithTheNamedError(string? fingerprint)
    {
        string deviceId = await SeedTvAsync(TestAuthHandler.DefaultUserId, fingerprint);

        HttpResponseMessage response = await _factory
            .CreateClient()
            .AsAuthenticated()
            .PostAsync(Url(deviceId), null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        JsonElement body = JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement;
        Assert.Equal("device_fingerprint_unknown", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WithoutAuthentication_IsUnauthorized()
    {
        string deviceId = await SeedTvAsync(TestAuthHandler.DefaultUserId, "placeholder-fp-b");

        HttpResponseMessage response = await _factory
            .CreateClient()
            .AsUnauthenticated()
            .PostAsync(Url(deviceId), null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A token carries the device it was minted for. Swapping Y's claims into X's
    /// expectation must not pass.
    /// </summary>
    [Fact]
    public async Task TokenForDeviceX_CarriesXsDeviceIdAndFingerprint_NotY()
    {
        string fpX = "placeholder-fp-x-" + Guid.NewGuid().ToString("N");
        string fpY = "placeholder-fp-y-" + Guid.NewGuid().ToString("N");
        string x = await SeedTvAsync(TestAuthHandler.DefaultUserId, fpX);
        string y = await SeedTvAsync(TestAuthHandler.DefaultUserId, fpY);

        JsonElement claimsX = Part(await MintAsync(x), 1);
        JsonElement claimsY = Part(await MintAsync(y), 1);

        Assert.Equal(x, claimsX.GetProperty("device_id").GetString());
        Assert.Equal(fpX, claimsX.GetProperty("fp").GetString());
        Assert.NotEqual(y, claimsX.GetProperty("device_id").GetString());
        Assert.NotEqual(fpY, claimsX.GetProperty("fp").GetString());
        Assert.Equal(y, claimsY.GetProperty("device_id").GetString());
        Assert.Equal(fpY, claimsY.GetProperty("fp").GetString());
    }

    /// <summary>
    /// The mint route is a literal path; the proxy's {deviceId}/{**path} catch-all
    /// must not swallow it. A swallowed POST would be forwarded to a TV (or 404
    /// "no owned TV") instead of answering with a token.
    /// </summary>
    [Fact]
    public async Task TheTokenRoute_WinsOverTheProxyCatchAll()
    {
        string deviceId = await SeedTvAsync(TestAuthHandler.DefaultUserId, "placeholder-fp-c");

        string token = await MintAsync(deviceId);

        Assert.Equal(3, token.Split('.').Length);
    }

    [Fact]
    public async Task TheKey_IsPersisted_SoARestartKeepsTheSameKid()
    {
        CastControlTokenService first = new(new CastControlKeyStore());
        string kid = JsonDocument
            .Parse(first.PublicJwksJson())
            .RootElement.GetProperty("keys")[0]
            .GetProperty("kid")
            .GetString()!;

        CastControlTokenService afterRestart = new(new CastControlKeyStore());
        string kidAfter = JsonDocument
            .Parse(afterRestart.PublicJwksJson())
            .RootElement.GetProperty("keys")[0]
            .GetProperty("kid")
            .GetString()!;

        Assert.Equal(kid, kidAfter);
        Assert.Equal(kid, PublishedKey().GetProperty("kid").GetString());
        await Task.CompletedTask;
    }
}
