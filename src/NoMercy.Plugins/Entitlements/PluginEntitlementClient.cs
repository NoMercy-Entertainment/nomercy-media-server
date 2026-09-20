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

using System.Text.Json;
using Microsoft.Extensions.Logging;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Verification;

namespace NoMercy.Plugins.Entitlements;

/// <summary>
/// Brings the bundle in, whether this server asked for it or NoMercy pushed it.
/// <para>
/// A bundle that does not verify is discarded and the old one kept. Otherwise
/// anyone who can answer for that address could take away a plugin the owner
/// paid for, which is the failure that costs a customer rather than a restart.
/// </para>
/// </summary>
public class PluginEntitlementClient(
    HttpClient http,
    IPluginEntitlementStore store,
    IPluginTrustedKeys trustedKeys,
    ILogger<PluginEntitlementClient> logger
)
{
    public async Task RefreshAsync(Uri address, CancellationToken ct = default)
    {
        try
        {
            Accept(await http.GetStringAsync(address, ct));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Plugin entitlements: the refresh failed, so the bundle already on this server was kept."
            );
        }
    }

    /// <summary>
    /// The same landing for a bundle NoMercy pushed as for one this server
    /// fetched. A push that skipped the signature check would be a way in that
    /// the fetch does not have.
    /// </summary>
    public bool Accept(string body)
    {
        if (!TryRead(body, out PluginEntitlementBundle? bundle))
        {
            logger.LogWarning(
                "Plugin entitlements: the bundle did not verify, so the one already on this server was kept."
            );

            return false;
        }

        store.Save(bundle!);

        return true;
    }

    public bool TryRead(string body, out PluginEntitlementBundle? bundle)
    {
        bundle = null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;

            if (
                !PluginSignedEnvelope.Verifies(
                    root,
                    trustedKeys,
                    "server_id",
                    "issued_at",
                    "refresh_by",
                    "entitlements"
                )
            )
                return false;

            bundle = new(
                Ulid.Parse(root.GetProperty("server_id").GetString()!),
                root.GetProperty("issued_at").GetDateTimeOffset(),
                root.GetProperty("refresh_by").GetDateTimeOffset(),
                [
                    .. root.GetProperty("entitlements")
                        .EnumerateArray()
                        .Select(entitlement => new PluginEntitlement(
                            Ulid.Parse(entitlement.GetProperty("plugin_id").GetString()!),
                            entitlement.GetProperty("user_id").GetGuid(),
                            entitlement.GetProperty("tier").GetString() == "paid"
                                ? PluginTier.Paid
                                : PluginTier.Free,
                            entitlement.TryGetProperty("seats", out JsonElement seats)
                            && seats.ValueKind == JsonValueKind.Number
                                ? seats.GetInt32()
                                : null,
                            entitlement.TryGetProperty("expires_at", out JsonElement expires)
                            && expires.ValueKind == JsonValueKind.String
                                ? expires.GetDateTimeOffset()
                                : null
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
