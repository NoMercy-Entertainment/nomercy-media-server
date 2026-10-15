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

using Microsoft.Extensions.Logging;

namespace NoMercy.PluginSdk.Logging;

/// <summary>
/// The loggers a plugin's own lines go through, one per plugin.
/// <para>
/// Each plugin is a log entry of its own, named after the plugin, in the shared
/// Plugins group. Without this every plugin line read as a line of the server,
/// and an owner could not tell which plugin said what.
/// </para>
/// </summary>
public interface IPluginLoggers
{
    /// <summary>
    /// Names the plugin in the server log and the dashboard log view. Called
    /// when the plugin loads. Calling again renames it.
    /// </summary>
    void Register(Ulid pluginId, string displayName);

    /// <summary>The logger the plugin's own lines go through.</summary>
    ILogger For(Ulid pluginId);
}
