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

namespace NoMercy.PluginSdk.Sideload;

/// <summary>
/// Whether the owner has turned on installing from a file.
/// <para>
/// A seam rather than a static read, because a route that answers differently
/// depending on a file in the server's data folder is a route nothing can
/// check. Read per call, so turning it off takes effect on the next install.
/// </para>
/// </summary>
public interface IPluginDeveloperModeSource
{
    bool Enabled { get; }
}

/// <summary>The owner's saved answer, read from disk each time it is asked.</summary>
public sealed class PluginDeveloperModeFile(string? folder = null) : IPluginDeveloperModeSource
{
    public bool Enabled => PluginDeveloperMode.Load(folder).Enabled;
}
