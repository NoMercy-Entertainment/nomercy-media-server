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
using System.Text;
using System.Text.Json;
using NoMercy.Database;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api;

/// <summary>
/// A TV fetches the server's public key to check a control token. The route is public,
/// like /info, because the TV holds no Keycloak token of the server's users.
/// </summary>
[Trait("Category", "Integration")]
public class CastControlKeysTests : IClassFixture<NoMercyApiFactory>
{
    private const string KeysUrl = "/api/v1/cast/keys";
    private readonly NoMercyApiFactory _factory;

    public CastControlKeysTests(NoMercyApiFactory factory)
    {
        _factory = factory;
    }

    private async Task<JsonElement> FetchKeysAsync(HttpClient client)
    {
        HttpResponseMessage response = await client.GetAsync(KeysUrl);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task Keys_AnswerWithoutAuthentication_AsAJwksWithOneSigningKey()
    {
        JsonElement jwks = await FetchKeysAsync(_factory.CreateClient().AsUnauthenticated());

        JsonElement key = Assert.Single(jwks.GetProperty("keys").EnumerateArray());
        Assert.Equal("EC", key.GetProperty("kty").GetString());
        Assert.Equal("P-256", key.GetProperty("crv").GetString());
        Assert.Equal("sig", key.GetProperty("use").GetString());
        Assert.Equal("ES256", key.GetProperty("alg").GetString());
        Assert.False(string.IsNullOrEmpty(key.GetProperty("x").GetString()));
        Assert.False(string.IsNullOrEmpty(key.GetProperty("y").GetString()));
        Assert.False(string.IsNullOrEmpty(key.GetProperty("kid").GetString()));
        Assert.False(key.TryGetProperty("d", out _), "the private key must never be published");
    }

    [Fact]
    public async Task PublishedKid_MatchesTheHeaderOfAMintedToken()
    {
        string deviceId = "cast-keys-" + Guid.NewGuid().ToString("N");
        await using (MediaContext db = new())
        {
            db.Devices.Add(
                new()
                {
                    DeviceId = deviceId,
                    Type = "tv",
                    Name = "Placeholder TV",
                    OwnerUserId = TestAuthHandler.DefaultUserId,
                    Fingerprint = "placeholder-fp-keys",
                    LanIp = "203.0.113.11",
                }
            );
            await db.SaveChangesAsync();
        }

        HttpResponseMessage minted = await _factory
            .CreateClient()
            .AsAuthenticated()
            .PostAsync($"/api/v1/cast/{deviceId}/token", null);
        Assert.Equal(HttpStatusCode.OK, minted.StatusCode);
        string token = JsonDocument
            .Parse(await minted.Content.ReadAsStringAsync())
            .RootElement.GetProperty("token")
            .GetString()!;
        string headerSegment = token.Split('.')[0];
        string padded = headerSegment.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        string headerKid = JsonDocument
            .Parse(Encoding.UTF8.GetString(Convert.FromBase64String(padded)))
            .RootElement.GetProperty("kid")
            .GetString()!;

        JsonElement jwks = await FetchKeysAsync(_factory.CreateClient().AsUnauthenticated());

        Assert.Equal(headerKid, jwks.GetProperty("keys")[0].GetProperty("kid").GetString());
    }
}
