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

namespace NoMercy.Plugins.Capabilities;

/// <summary>
/// The one place that decides whether a plugin may do a thing.
/// <para>
/// Three questions in order, because the answer to each is a different fix for
/// the author and a different sentence for the owner. Did the manifest declare
/// it, did the owner consent to it, and does the scope cover this particular
/// value. Collapsed into one check, an author adds a line to plugin.json and
/// nothing changes, because the real answer was the owner never approved it.
/// </para>
/// </summary>
public interface IPluginCapabilityBroker
{
    /// <summary>
    /// Null when the plugin may proceed. Otherwise the refusal to raise, with
    /// the count already recorded.
    /// </summary>
    PluginRefusal? Check(Ulid pluginId, string capability, string? scopeValue = null);
}
