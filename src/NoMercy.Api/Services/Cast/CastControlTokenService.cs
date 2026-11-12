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

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NoMercy.Api.Services.Cast;

/// <summary>A minted control token and the instant it stops being valid.</summary>
public sealed record CastControlToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Mints the short ES256 token a phone presents to one TV instead of its full Keycloak
/// token, and publishes the matching public key. The private key is made once, kept in
/// the server's protected configuration and never logged or sent anywhere.
/// </summary>
public sealed class CastControlTokenService
{
    public const string Audience = "tv-control";
    public const int LifetimeSeconds = 600;

    private readonly ECDsa _key;
    private readonly string _kid;
    private readonly string _jwksJson;
    private readonly TimeProvider _time;

    public CastControlTokenService(ICastControlKeyStore store, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _key = LoadOrCreate(store);

        ECParameters publicParameters = _key.ExportParameters(false);
        byte[] x = publicParameters.Q.X!;
        byte[] y = publicParameters.Q.Y!;
        _kid = Base64Url(SHA256.HashData([.. x, .. y])[..16]);
        _jwksJson = JsonSerializer.Serialize(
            new
            {
                keys = new[]
                {
                    new
                    {
                        kty = "EC",
                        crv = "P-256",
                        x = Base64Url(x),
                        y = Base64Url(y),
                        kid = _kid,
                        use = "sig",
                        alg = "ES256",
                    },
                },
            }
        );
    }

    public string PublicJwksJson() => _jwksJson;

    public CastControlToken Mint(string issuer, Guid subject, string deviceId, string fingerprint)
    {
        DateTimeOffset issuedAt = _time.GetUtcNow();
        DateTimeOffset expiresAt = issuedAt.AddSeconds(LifetimeSeconds);

        string header = Base64Url(
            JsonSerializer.SerializeToUtf8Bytes(
                new Dictionary<string, string>
                {
                    ["alg"] = "ES256",
                    ["typ"] = "JWT",
                    ["kid"] = _kid,
                }
            )
        );
        string payload = Base64Url(
            JsonSerializer.SerializeToUtf8Bytes(
                new Dictionary<string, object>
                {
                    ["iss"] = issuer,
                    ["sub"] = subject.ToString(),
                    ["aud"] = Audience,
                    ["scope"] = Audience,
                    ["device_id"] = deviceId,
                    ["fp"] = fingerprint,
                    ["iat"] = issuedAt.ToUnixTimeSeconds(),
                    ["exp"] = expiresAt.ToUnixTimeSeconds(),
                    ["jti"] = Guid.NewGuid().ToString("N"),
                }
            )
        );

        string signingInput = $"{header}.{payload}";
        byte[] signature = _key.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation
        );

        return new($"{signingInput}.{Base64Url(signature)}", expiresAt);
    }

    private static ECDsa LoadOrCreate(ICastControlKeyStore store)
    {
        ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string? pem = store.Load();
        if (!string.IsNullOrWhiteSpace(pem))
        {
            key.ImportFromPem(pem);
            return key;
        }

        store.Save(key.ExportPkcs8PrivateKeyPem());
        return key;
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
