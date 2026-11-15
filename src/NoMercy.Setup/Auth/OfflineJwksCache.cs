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
using Microsoft.IdentityModel.Tokens;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using Serilog.Events;

namespace NoMercy.Setup.Auth;

public static class OfflineJwksCache
{
    // LOCAL-ONLY: OfflineJwksCache is called during DI registration (ConfigureAuth) and
    // AuthManager.InitializeAsync — both run before StorageProvider is initialized.
    private static readonly IStorageDriver Backend = new LocalStorageDriver();
    private static readonly object CacheLock = new();

    public static RsaSecurityKey? CachedSigningKey
    {
        get
        {
            lock (CacheLock)
            {
                return field;
            }
        }
        private set
        {
            lock (CacheLock)
            {
                field = value;
            }
        }
    }

    private static volatile IReadOnlyList<SecurityKey> _jwksKeys = [];

    public static IReadOnlyList<SecurityKey> CachedSigningKeys => _jwksKeys;

    // Fetches only from the configured realm over https; a failure keeps the old cache.
    public static async Task<bool> RefreshAsync(HttpClient client, CancellationToken ct = default)
    {
        string certsUrl =
            $"{ExternalServicesConfig.Current.AuthBaseUrl}protocol/openid-connect/certs";
        try
        {
            if (
                !Uri.TryCreate(certsUrl, UriKind.Absolute, out Uri? uri)
                || uri.Scheme != Uri.UriSchemeHttps
            )
            {
                Logger.Auth(
                    $"Refusing to fetch the realm JWKS over a non-https URL: {certsUrl}",
                    LogEventLevel.Warning
                );
                return false;
            }

            using HttpResponseMessage response = await client.GetAsync(uri, ct);
            response.EnsureSuccessStatusCode();
            Uri? finalUri = response.RequestMessage?.RequestUri;
            if (finalUri is not null && finalUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("the JWKS request ended on a non-https URL");

            string json = await response.Content.ReadAsStringAsync(ct);
            List<SecurityKey> keys = [.. new JsonWebKeySet(json).GetSigningKeys()];
            if (keys.Count == 0)
                throw new InvalidOperationException("the realm JWKS holds no signing keys");

            using Stream stream = Backend.OpenWrite(AppFiles.JwksCacheFile, overwrite: true);
            using StreamWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(json);
            _jwksKeys = keys;
            Logger.Auth($"Cached {keys.Count} realm signing keys for offline use");
            return true;
        }
        catch (Exception e)
        {
            Logger.Auth(
                $"Failed to refresh the realm JWKS, keeping the cached keys: {e.Message}",
                LogEventLevel.Warning
            );
            return false;
        }
    }

    public static void CachePublicKey(string publicKeyBase64)
    {
        try
        {
            using Stream stream = Backend.OpenWrite(AppFiles.AuthKeysFile, overwrite: true);
            using StreamWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(publicKeyBase64);
            CachedSigningKey = CreateSecurityKeyFromBase64(publicKeyBase64);
            Logger.Auth("Cached auth public key for offline use");
        }
        catch (Exception e)
        {
            Logger.Auth($"Failed to cache auth public key: {e.Message}", LogEventLevel.Warning);
        }
    }

    public static bool LoadCachedPublicKey()
    {
        try
        {
            bool jwksLoaded = LoadCachedJwks();
            if (!Backend.FileExists(AppFiles.AuthKeysFile))
                return jwksLoaded;

            string publicKeyBase64;
            using (StreamReader reader = new(Backend.OpenRead(AppFiles.AuthKeysFile)))
                publicKeyBase64 = reader.ReadToEnd().Trim();

            if (string.IsNullOrEmpty(publicKeyBase64))
                return false;

            CachedSigningKey = CreateSecurityKeyFromBase64(publicKeyBase64);
            Logger.Auth("Loaded cached auth public key for offline validation");
            return true;
        }
        catch (Exception e)
        {
            Logger.Auth(
                $"Failed to load cached auth public key: {e.Message}",
                LogEventLevel.Warning
            );
            return false;
        }
    }

    private static bool LoadCachedJwks()
    {
        try
        {
            if (!Backend.FileExists(AppFiles.JwksCacheFile))
                return false;

            using StreamReader reader = new(Backend.OpenRead(AppFiles.JwksCacheFile));
            List<SecurityKey> keys = [.. new JsonWebKeySet(reader.ReadToEnd()).GetSigningKeys()];
            if (keys.Count == 0)
                return false;

            _jwksKeys = keys;
            Logger.Auth($"Loaded {keys.Count} cached realm signing keys for offline validation");
            return true;
        }
        catch (Exception e)
        {
            Logger.Auth($"Failed to load cached realm JWKS: {e.Message}", LogEventLevel.Warning);
            return false;
        }
    }

    internal static RsaSecurityKey CreateSecurityKeyFromBase64(string publicKeyBase64)
    {
        string cleaned = publicKeyBase64
            .Replace("-----BEGIN PUBLIC KEY-----", "")
            .Replace("-----END PUBLIC KEY-----", "")
            .Replace("-----BEGIN RSA PUBLIC KEY-----", "")
            .Replace("-----END RSA PUBLIC KEY-----", "")
            .Replace("\n", "")
            .Replace("\r", "")
            .Trim();

        byte[] keyBytes = Convert.FromBase64String(cleaned);
        RSA rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(keyBytes, out _);
        return new(rsa);
    }
}
