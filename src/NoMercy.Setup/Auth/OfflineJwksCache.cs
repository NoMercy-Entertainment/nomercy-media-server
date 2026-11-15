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

    public static IReadOnlyList<SecurityKey> CachedSigningKeys => [];

    public static Task<bool> RefreshAsync(HttpClient client, CancellationToken ct = default) =>
        Task.FromResult(false);

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
            if (!Backend.FileExists(AppFiles.AuthKeysFile))
                return false;

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
