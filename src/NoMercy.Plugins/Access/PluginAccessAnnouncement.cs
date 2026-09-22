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

using NoMercy.Events;
using NoMercy.Events.Plugins;

namespace NoMercy.PluginSdk.Access;

/// <summary>
/// How a gate says its answer may be different now.
/// <para>
/// One line at each of the four places, rather than each of them building the
/// event. A gate's job is to decide; saying so is the same sentence every
/// time, and a sentence written four times is written three ways.
/// </para>
/// </summary>
public static class PluginAccessAnnouncement
{
    public static void Changed(IEventBus? events, Ulid? pluginId = null) =>
        _ = events?.PublishAsync(new PluginAccessChangedEvent { PluginId = pluginId?.ToString() });
}
