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

namespace NoMercy.Plugins.Guests;

/// <summary>Which installs belong to one person rather than to the server.</summary>
public interface IPluginGuestInstallStore
{
    void Record(Ulid pluginId, Guid guestId);

    /// <summary>The guest this install belongs to, or null when it is the server's.</summary>
    Guid? GuestFor(Ulid pluginId);

    IReadOnlyList<Ulid> PluginsFor(Guid guestId);

    void Forget(Ulid pluginId);
}
