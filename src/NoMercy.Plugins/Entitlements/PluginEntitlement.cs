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

/// <summary>One person's right to run one paid plugin on this server.</summary>
/// <param name="Seats">How many members a shared license covers, or null for every member.</param>
/// <param name="ExpiresAt">When a subscription lapses, or null for a purchase that does not.</param>
public sealed record PluginEntitlement(
    Ulid PluginId,
    Guid UserId,
    PluginTier Tier,
    int? Seats,
    DateTimeOffset? ExpiresAt
);
