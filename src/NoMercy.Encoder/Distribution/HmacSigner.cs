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
/// Signs and verifies HTTP requests for distributed encoder communication.
///
/// String to sign: "{METHOD}\n{PATH}\n{TIMESTAMP}\n{base64(sha256(body))}"
/// Output signature: base64(hmac_sha256(secret, stringToSign))
///
/// Constant-time comparison via <see cref="CryptographicOperations.FixedTimeEquals"/>
/// prevents timing-oracle attacks.
/// </summary>
public sealed class HmacSigner(string secret)
{
    private readonly byte[] _key = Encoding.UTF8.GetBytes(secret);

    /// <summary>
    /// Produces the base64-encoded HMAC-SHA256 signature for the given request primitives.
    /// </summary>
    public string Sign(string method, string path, long timestamp, byte[] body)
    {
        string stringToSign = BuildStringToSign(method, path, timestamp, body);
        using HMACSHA256 hmac = new(_key);
        byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign));
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// Verifies a signature against the expected one derived from the request primitives.
    /// Returns false when:
    /// - the signature does not match (wrong key, tampered body, wrong method/path),
    /// - or the timestamp is more than <paramref name="replayWindow"/> away from now, in
    ///   either direction. The past side limits replays; the future side lets a signer
    ///   whose clock runs a little ahead of this machine through.
    /// </summary>
    public bool Verify(
        string method,
        string path,
        long timestamp,
        byte[] body,
        string signature,
        TimeSpan replayWindow
    )
    {
        long nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long windowSeconds = (long)replayWindow.TotalSeconds;

        // Compared as bounds, not as |now - timestamp|: the timestamp comes from a
        // request header, and a value near long.MinValue would overflow the subtraction.
        if (timestamp < nowSeconds - windowSeconds || timestamp > nowSeconds + windowSeconds)
            return false;

        string expected = Sign(method, path, timestamp, body);

        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        byte[] actualBytes = Encoding.UTF8.GetBytes(signature);

        if (expectedBytes.Length != actualBytes.Length)
            return false;

        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static string BuildStringToSign(string method, string path, long timestamp, byte[] body)
    {
        string bodyHash = Convert.ToBase64String(SHA256.HashData(body));
        return $"{method.ToUpperInvariant()}\n{path}\n{timestamp}\n{bodyHash}";
    }
}
