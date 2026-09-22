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

using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginSdk.Entitlements;

/// <summary>
/// Asked before a paid plugin runs: has this server been told it may.
/// <para>
/// A week of grace after the bundle should have refreshed, then the plugin
/// goes dormant: installed, visible, not running. Dormant is deliberately not
/// uninstalled. Someone whose internet is down has not stopped paying, and
/// deleting what they bought to make a point about a network is not a thing
/// this server does.
/// </para>
/// </summary>
public class PluginEntitlementGate(IPluginEntitlementStore store, TimeProvider clock, Guid ownerId)
{
    public const int GraceDays = 7;

    public PluginRefusal? Check(Ulid pluginId, PluginTier tier)
    {
        if (tier != PluginTier.Paid)
            return null;

        DateTimeOffset now = clock.GetUtcNow();
        PluginEntitlementBundle bundle = store.Current;
        PluginEntitlement? entitlement = bundle.For(pluginId, ownerId, now);
        bool overdue = now - bundle.RefreshBy > TimeSpan.FromDays(GraceDays);

        if (entitlement is not null && !overdue)
            return null;

        // Order matters: an owner who holds the entitlement is told about the
        // clock, and an owner who does not is told they need to buy it. The
        // other way round sends a paying customer to a shop they already used.
        if (entitlement is not null)
            return new(
                PluginRefusalCodes.EntitlementDormant,
                pluginId.ToString(),
                "The plugin is installed and not running.",
                $"This server has not refreshed its entitlements for more than {GraceDays} days, so it cannot confirm the purchase.",
                "Connect the server to the internet. Nothing is deleted while a plugin is dormant, and it starts again on the next successful check.",
                PluginRefusalSeverity.Blocked
            );

        return new(
            PluginRefusalCodes.EntitlementMissing,
            pluginId.ToString(),
            "The plugin did not start.",
            "This is a paid plugin and the owner of this server holds no entitlement for it.",
            "Buy it on nomercy.tv with the account that owns this server, then refresh the plugin list.",
            PluginRefusalSeverity.Blocked
        );
    }
}
