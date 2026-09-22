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

namespace NoMercy.PluginSdk.OutOfProcess;

/// <summary>
/// What the loader asks before it loads an assembly into the server.
/// <para>
/// The owner's isolation choice was written to disk and read by nothing. This
/// is the one question that makes it mean something at load time, kept behind
/// an interface so the loader does not have to know about processes, sockets
/// or sandboxes to ask it.
/// </para>
/// </summary>
public interface IPluginRemoteLoader
{
    /// <summary>Where this plugin is meant to run.</summary>
    PluginIsolation IsolationFor(Ulid pluginId);

    /// <summary>
    /// Starts it elsewhere and hands back the plugin the registry will hold.
    /// <para>
    /// Null when this install cannot do it, so the loader falls back to its
    /// own process rather than leaving the owner with no plugin at all. The
    /// dashboard is what says the choice is not being honored yet.
    /// </para>
    /// </summary>
    Task<IPlugin?> LoadAsync(PluginDescription description, CancellationToken ct = default);
}
