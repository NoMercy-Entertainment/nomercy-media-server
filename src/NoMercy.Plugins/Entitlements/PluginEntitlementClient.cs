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
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Verification;

namespace NoMercy.PluginSdk.Entitlements;

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
    /// <summary>
    /// The server endpoint answers only its owner, so the server's own access
    /// token rides along when it holds one.
    /// </summary>
    public async Task RefreshAsync(
        Uri address,
        string? bearerToken = null,
        CancellationToken ct = default
    )
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, address);

            if (bearerToken is { Length: > 0 })
                request.Headers.Authorization = new("Bearer", bearerToken);

            using HttpResponseMessage response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            Accept(await response.Content.ReadAsStringAsync(ct));
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

    /// <summary>
    /// nomercy.tv names a server by the uuid it registered with. A ulid is
    /// read too, since that is what an earlier bundle carried.
    /// </summary>
    private static Ulid ServerId(string value) =>
        Guid.TryParse(value, out Guid uuid) ? new(uuid) : Ulid.Parse(value);

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
                ServerId(root.GetProperty("server_id").GetString()!),
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
