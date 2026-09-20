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
using Microsoft.Extensions.Logging;
using NoMercy.Plugins.Verification;

namespace NoMercy.Plugins.Revocation;

/// <summary>
/// Refreshes the list from NoMercy and keeps the old one when anything about
/// the answer is wrong.
/// <para>
/// A list that does not verify is discarded rather than stored. Otherwise
/// anyone who can answer for that address could pause every plugin on a
/// server, or say nothing was revoked when something was.
/// </para>
/// </summary>
public class PluginRevocationClient(
    HttpClient http,
    IPluginRevocationStore store,
    IPluginTrustedKeys trustedKeys,
    ILogger<PluginRevocationClient> logger
)
{
    public async Task RefreshAsync(Uri address, CancellationToken ct = default)
    {
        try
        {
            string body = await http.GetStringAsync(address, ct);

            if (!TryRead(body, out PluginRevocationList? list))
            {
                logger.LogWarning(
                    "Plugin revocations: the list did not verify, so the one already on this server was kept."
                );

                return;
            }

            store.Save(list!);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Plugin revocations: the refresh failed, so the list already on this server was kept."
            );
        }
    }

    /// <summary>
    /// The signed payload is the issue time followed by the entries array
    /// exactly as it was written. Signing the parsed form instead would let a
    /// sender reorder or reformat what we read without breaking the signature.
    /// </summary>
    public bool TryRead(string body, out PluginRevocationList? list)
    {
        list = null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;

            if (
                !root.TryGetProperty("signature", out JsonElement signature)
                || !root.TryGetProperty("issued_at", out JsonElement issuedAt)
                || !root.TryGetProperty("entries", out JsonElement entries)
            )
                return false;

            string algorithm = signature.GetProperty("alg").GetString() ?? string.Empty;

            if (
                !string.Equals(
                    algorithm,
                    PluginEd25519.Algorithm,
                    StringComparison.OrdinalIgnoreCase
                )
            )
                return false;

            string? publicKey = trustedKeys.Find(
                signature.GetProperty("kid").GetString() ?? string.Empty
            );

            byte[] signed = Encoding.UTF8.GetBytes($"{issuedAt.GetString()}{entries.GetRawText()}");

            // One condition, because a key this server does not hold and a
            // signature that does not verify are the same answer: we cannot say
            // NoMercy sent this.
            if (
                publicKey is null
                || !PluginEd25519.Verify(
                    signed,
                    signature.GetProperty("value").GetString() ?? string.Empty,
                    publicKey
                )
            )
                return false;

            list = new(
                issuedAt.GetDateTimeOffset(),
                [
                    .. entries
                        .EnumerateArray()
                        .Select(entry => new PluginRevocationEntry(
                            Ulid.Parse(entry.GetProperty("plugin_id").GetString()!),
                            entry.GetProperty("hash").GetString() ?? string.Empty,
                            entry.GetProperty("reason").GetString() ?? string.Empty
                        )),
                ]
            );

            return true;
        }
        catch (Exception exception)
            when (exception is JsonException or KeyNotFoundException or FormatException)
        {
            return false;
        }
    }
}
