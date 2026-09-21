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

namespace NoMercy.Plugins.Sideload;

/// <summary>
/// Reads and writes the owner's answer about developer mode.
/// <para>
/// The refusal for a blocked sideload tells the owner to turn this on in
/// server settings. Until this existed the only thing that could was a text
/// editor, so the sentence named a place that was not there.
/// </para>
/// </summary>
public sealed class PluginDeveloperModeStore(string? folder = null)
{
    public PluginDeveloperMode Read()
    {
        return PluginDeveloperMode.Load(folder);
    }

    public PluginDeveloperMode Write(bool enabled)
    {
        PluginDeveloperMode next = new(enabled);
        next.Save(folder);

        return next;
    }
}
