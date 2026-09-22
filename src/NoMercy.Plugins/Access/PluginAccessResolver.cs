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

namespace NoMercy.PluginSdk.Access;

/// <summary>
/// Whether one account may see one plugin on this server, and in what sense.
/// <para>
/// One resolver rather than a check per screen. A listing that hides a plugin
/// and a page that opens it have to agree, or someone is offered something
/// that then refuses them.
/// </para>
/// </summary>
public class PluginAccessResolver(
    IPluginInstallFacts facts,
    IPluginEntitlementStore entitlements,
    IPluginMembership membership,
    TimeProvider clock,
    Func<Guid> ownerId
) : IPluginAccessResolver
{
    public PluginAccess Resolve(Ulid pluginId, Guid userId)
    {
        // Nobody is an empty guid. A caller the server could not identify and
        // a server whose owner is not set both read as one, and comparing them
        // would hand an unauthenticated request everything the owner has.
        if (userId == Guid.Empty)
            return PluginAccess.None;

        Guid owner = ownerId();

        // Installed for one guest. Theirs and nobody else's, the owner
        // included: it was never the server's plugin.
        if (facts.GuestFor(pluginId) is { } guest)
            return guest == userId ? PluginAccess.Owned : PluginAccess.None;

        // A file the owner supplied. Nothing can say who wrote it, so it is
        // not something to hand to the other people on the server.
        if (facts.IsSideloaded(pluginId))
            return userId == owner ? PluginAccess.Owned : PluginAccess.None;

        // Asked before anything about this server, because an entitlement is
        // the caller's own wherever they are signed in.
        if (Holds(pluginId, userId))
            return PluginAccess.Owned;

        if (facts.TierOf(pluginId) != PluginTier.Paid)
        {
            if (userId == owner)
                return PluginAccess.Owned;

            return membership.IsAcceptedMember(userId) ? PluginAccess.Shared : PluginAccess.None;
        }

        PluginEntitlement? held = entitlements.Current.For(pluginId, owner, clock.GetUtcNow());

        if (held is null)
            return PluginAccess.None;

        if (userId == owner)
            return PluginAccess.Owned;

        if (!membership.IsAcceptedMember(userId))
            return PluginAccess.None;

        // A seat count is the publisher's, and null means the whole household.
        if (held.Seats is { } seats && membership.SeatsTakenFor(pluginId) >= seats)
            return PluginAccess.None;

        return PluginAccess.Shared;
    }

    private bool Holds(Ulid pluginId, Guid userId) =>
        entitlements.Current.For(pluginId, userId, clock.GetUtcNow()) is not null;
}
