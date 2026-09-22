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
using NoMercy.PluginSdk.Entitlements;

namespace NoMercy.PluginSdk.Sideload;

/// <summary>
/// A plugin installed from a file is the owner's own risk, taken on purpose.
/// <para>
/// It is never shared with the other people on the server and never counts as
/// verified, because the server cannot say who wrote it. A paid id stays
/// refused without an entitlement whatever the owner switches on: developer
/// mode is for someone building a plugin, not a way around buying one.
/// </para>
/// </summary>
public class PluginSideloadPolicy(
    Func<bool> developerMode,
    IPluginEntitlementStore entitlements,
    TimeProvider clock,
    Func<Guid> ownerId
)
{
    public const bool SharedWithMembers = false;
    public const bool Verified = false;

    public PluginRefusal? Check(PluginManifest manifest)
    {
        string who = $"{manifest.Name} {manifest.Version}";

        if (!developerMode())
            return new(
                PluginRefusalCodes.SideloadDisabled,
                who,
                "The server did not install the file.",
                "Installing a plugin from a file is off, because the server cannot check who wrote it.",
                "Turn on developer mode in server settings, read the warning there, then install the file again.",
                PluginRefusalSeverity.Blocked
            );

        if (manifest.Tier != PluginTier.Paid)
            return null;

        if (entitlements.Current.For(manifest.Id.Value, ownerId(), clock.GetUtcNow()) is not null)
            return null;

        return new(
            PluginRefusalCodes.SideloadPaidId,
            who,
            "The server did not install the file.",
            "This id belongs to a paid plugin on the marketplace and the owner of this server holds no entitlement for it.",
            "Buy it on nomercy.tv with the account that owns this server, then install it from the marketplace.",
            PluginRefusalSeverity.Blocked
        );
    }
}
