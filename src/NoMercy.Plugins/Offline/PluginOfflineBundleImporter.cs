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
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Entitlements;
using NoMercy.PluginSdk.Revocation;
using NoMercy.PluginSdk.Verification;

namespace NoMercy.PluginSdk.Offline;

/// <summary>
/// Lands one file carried in from a device that is online: the entitlements
/// and the revocation list a connected server would have fetched.
/// <para>
/// Both or neither. A bundle that fails any check saves nothing, so a refused
/// import cannot leave the server holding half of an answer and calling it
/// current.
/// </para>
/// </summary>
public class PluginOfflineBundleImporter(
    IPluginEntitlementStore entitlements,
    IPluginRevocationStore revocations,
    IPluginTrustedKeys trustedKeys,
    TimeProvider clock
)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public PluginRefusal? Import(Stream bundle)
    {
        using StreamReader reader = new(bundle, Encoding.UTF8);

        return Import(reader.ReadToEnd());
    }

    public PluginRefusal? Import(string body)
    {
        JsonElement root;
        JsonDocument? document = null;

        try
        {
            document = JsonDocument.Parse(body);
            root = document.RootElement;

            if (
                !PluginSignedEnvelope.Verifies(
                    root,
                    trustedKeys,
                    "issued_at",
                    "valid_days",
                    "entitlements",
                    "revocations"
                )
            )
                return NotSigned();

            DateTimeOffset issuedAt = root.GetProperty("issued_at").GetDateTimeOffset();
            int validDays = root.GetProperty("valid_days").GetInt32();

            if (clock.GetUtcNow() - issuedAt > TimeSpan.FromDays(validDays))
                return new(
                    PluginRefusalCodes.OfflineBundleExpired,
                    "offline bundle",
                    "The server did not accept the bundle.",
                    $"The bundle was issued more than {validDays} days ago.",
                    "Download a new bundle from nomercy.tv on a device that is online, and import that one instead.",
                    PluginRefusalSeverity.Blocked
                );

            PluginEntitlementBundle? held = JsonSerializer.Deserialize<PluginEntitlementBundle>(
                root.GetProperty("entitlements").GetRawText(),
                Json
            );

            PluginRevocationList? withdrawn = JsonSerializer.Deserialize<PluginRevocationList>(
                root.GetProperty("revocations").GetRawText(),
                Json
            );

            if (held is null || withdrawn is null)
                return NotSigned();

            // Read both before writing either. A bundle that parses halfway
            // would otherwise leave the entitlements replaced and the
            // revocation list untouched, which is a server running on two
            // different days' answers.
            entitlements.Save(held);
            revocations.Save(withdrawn);

            return null;
        }
        catch (Exception exception)
            when (exception is JsonException or KeyNotFoundException or FormatException)
        {
            return NotSigned();
        }
        finally
        {
            document?.Dispose();
        }
    }

    private static PluginRefusal NotSigned() =>
        new(
            PluginRefusalCodes.OfflineBundleInvalid,
            "offline bundle",
            "The server did not accept the bundle.",
            "The bundle is not signed by NoMercy, or it was edited after it was signed.",
            "Download the bundle again from nomercy.tv on a device that is online, and import the file unchanged.",
            PluginRefusalSeverity.Blocked
        );
}
