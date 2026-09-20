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

using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Guests;

namespace NoMercy.Plugins.Access;

/// <summary>
/// The installed plugins, read for the three facts access turns on.
/// <para>
/// A plugin the server does not know reads as paid, unsideloaded and owned by
/// nobody, which resolves to none for everyone. Unknown is the one case where
/// answering generously is the dangerous answer.
/// </para>
/// </summary>
public class PluginInstallFacts(IPluginManager plugins, IPluginGuestInstallStore guests)
    : IPluginInstallFacts
{
    public PluginTier TierOf(Ulid pluginId) =>
        plugins.GetPluginInfo(pluginId)?.Tier ?? PluginTier.Paid;

    public bool IsSideloaded(Ulid pluginId) => plugins.GetPluginInfo(pluginId)?.Sideloaded ?? false;

    public Guid? GuestFor(Ulid pluginId) => guests.GuestFor(pluginId);
}
