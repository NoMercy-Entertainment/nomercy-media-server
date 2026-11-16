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

namespace NoMercy.PluginSdk.Entitlements;

/// <summary>Everything this server was last told it is entitled to run.</summary>
public sealed record PluginEntitlementBundle(
    Ulid ServerId,
    DateTimeOffset IssuedAt,
    DateTimeOffset RefreshBy,
    IReadOnlyList<PluginEntitlement> Entitlements
)
{
    /// <summary>A server that has never asked. Reads as infinitely overdue.</summary>
    public static PluginEntitlementBundle None { get; } =
        new(Ulid.Empty, DateTimeOffset.MinValue, DateTimeOffset.MinValue, []);

    /// <summary>The entitlement that lets the plugin run: active and not lapsed.</summary>
    public PluginEntitlement? For(Ulid pluginId, Guid userId, DateTimeOffset now) =>
        Find(pluginId, userId, now, PluginEntitlementStatus.Active);

    /// <summary>A purchase nomercy.tv paused, for example while a dispute runs.</summary>
    public PluginEntitlement? SuspendedFor(Ulid pluginId, Guid userId, DateTimeOffset now) =>
        Find(pluginId, userId, now, PluginEntitlementStatus.Suspended);

    private PluginEntitlement? Find(
        Ulid pluginId,
        Guid userId,
        DateTimeOffset now,
        PluginEntitlementStatus status
    ) =>
        Entitlements.FirstOrDefault(entitlement =>
            entitlement.PluginId == pluginId
            && entitlement.UserId == userId
            && entitlement.Status == status
            && (entitlement.ExpiresAt is null || entitlement.ExpiresAt > now)
        );
}
