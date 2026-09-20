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

namespace NoMercy.Plugins.Access;

/// <summary>One answer, asked everywhere a plugin could be shown or opened.</summary>
public interface IPluginAccessResolver
{
    PluginAccess Resolve(Ulid pluginId, Guid userId);
}

/// <summary>How a plugin got onto this server, which changes who may see it.</summary>
public interface IPluginInstallFacts
{
    PluginTier TierOf(Ulid pluginId);

    bool IsSideloaded(Ulid pluginId);

    /// <summary>The guest this install belongs to, or null when it is the server's.</summary>
    Guid? GuestFor(Ulid pluginId);
}

/// <summary>Who belongs to this server, and how many seats a plugin has taken.</summary>
public interface IPluginMembership
{
    bool IsAcceptedMember(Guid userId);

    /// <summary>Seats claimed so far, first come first served.</summary>
    int SeatsTakenFor(Ulid pluginId);
}
