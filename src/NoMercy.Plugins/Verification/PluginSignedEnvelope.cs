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

using System.Text;
using System.Text.Json;

namespace NoMercy.PluginSdk.Verification;

/// <summary>
/// The shape NoMercy signs anything it sends this server: some fields, plus a
/// signature block naming the key that signed them.
/// <para>
/// The signed string is those fields in the order the caller names them, each
/// one exactly as it arrived. Signing the parsed form instead would let a
/// sender reorder or reformat what we read without breaking the signature.
/// </para>
/// </summary>
public static class PluginSignedEnvelope
{
    /// <summary>
    /// True when the block verifies against a key this server holds. False for
    /// everything else, malformed input included: a caller that cannot tell
    /// "unsigned" from "signed by a stranger" from "not JSON" is a caller that
    /// treats all three the same way, which is the only safe answer here.
    /// </summary>
    public static bool Verifies(
        JsonElement root,
        IPluginTrustedKeys trustedKeys,
        params string[] signedProperties
    )
    {
        if (!root.TryGetProperty("signature", out JsonElement signature))
            return false;

        if (
            !signature.TryGetProperty("alg", out JsonElement algorithm)
            || !signature.TryGetProperty("kid", out JsonElement keyId)
            || !signature.TryGetProperty("value", out JsonElement value)
        )
            return false;

        if (
            !string.Equals(
                algorithm.GetString(),
                PluginEd25519.Algorithm,
                StringComparison.OrdinalIgnoreCase
            )
        )
            return false;

        string? publicKey = trustedKeys.Find(keyId.GetString() ?? string.Empty);
        StringBuilder signed = new();

        foreach (string property in signedProperties)
        {
            if (!root.TryGetProperty(property, out JsonElement element))
                return false;

            signed.Append(
                element.ValueKind == JsonValueKind.String
                    ? element.GetString()
                    : element.GetRawText()
            );
        }

        // One condition, because a key this server does not hold and a
        // signature that does not verify are the same answer: we cannot say
        // NoMercy sent this.
        return publicKey is not null
            && PluginEd25519.Verify(
                Encoding.UTF8.GetBytes(signed.ToString()),
                value.GetString() ?? string.Empty,
                publicKey
            );
    }
}
