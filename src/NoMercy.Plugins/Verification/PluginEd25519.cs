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

using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace NoMercy.Plugins.Verification;

/// <summary>
/// Ed25519 verification, and nothing else.
/// <para>
/// Verify only. This server has no reason to hold a signing key and no place
/// to put one, so there is no signing method here to reach for by accident.
/// </para>
/// <para>
/// BouncyCastle because .NET does not ship Ed25519, and it is already a
/// dependency of two projects here rather than a new one taken on for this.
/// </para>
/// </summary>
public static class PluginEd25519
{
    public const string Algorithm = "ed25519";

    /// <summary>
    /// False for anything that does not verify, including a malformed key or
    /// signature. A throw here would be a difference an attacker can measure:
    /// a wrong-length key crashing and a wrong signature returning false tells
    /// them which of the two they got wrong, so the one catch answers both.
    /// </summary>
    public static bool Verify(
        ReadOnlySpan<byte> content,
        ReadOnlySpan<byte> signature,
        ReadOnlySpan<byte> publicKey
    )
    {
        try
        {
            Ed25519Signer signer = new();
            signer.Init(false, new Ed25519PublicKeyParameters(publicKey.ToArray(), 0));
            signer.BlockUpdate(content.ToArray(), 0, content.Length);

            return signer.VerifySignature(signature.ToArray());
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Base64 in, the same answer out. Malformed base64 is false rather than a
    /// throw, for the reason above.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> content, string signature, string publicKey) =>
        Verify(content, Decode(signature), Decode(publicKey));

    /// <summary>Empty for anything that is not base64, which never verifies.</summary>
    private static byte[] Decode(string value)
    {
        Span<byte> buffer = new byte[value.Length];

        return Convert.TryFromBase64String(value, buffer, out int written)
            ? buffer[..written].ToArray()
            : [];
    }
}
