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

namespace NoMercy.Encoder.Distribution;

/// <summary>
/// The HMAC-SHA256 signature over <c>{path}|{timestamp}</c> that lets a worker
/// download a source file from the coordinator.
/// </summary>
public static class SourcePathSignature
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    public static string Compute(byte[] key, string path, long timestamp)
    {
        using HMACSHA256 hmac = new(key);
        return Convert.ToBase64String(
            hmac.ComputeHash(Encoding.UTF8.GetBytes($"{path}|{timestamp}"))
        );
    }

    public static bool IsFresh(long timestamp, DateTimeOffset now) =>
        (now - DateTimeOffset.FromUnixTimeSeconds(timestamp)).Duration() <= MaxAge;

    public static bool Matches(byte[] key, string path, long timestamp, string signature) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(signature),
            Encoding.UTF8.GetBytes(Compute(key, path, timestamp))
        );
}
