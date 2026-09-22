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

using NoMercy.NmSystem.Information;

namespace NoMercy.PluginSdk.Ipc;

/// <summary>
/// Where the server and one plugin's process meet.
/// <para>
/// Named per plugin, so one plugin can never reach another's channel, and the
/// two directions are separate endpoints: the broker is what the plugin calls
/// into, the host is what the server calls out to.
/// </para>
/// </summary>
public static class PluginChannelEndpoints
{
    public static string RunDirectoryFor(Ulid pluginId) =>
        Path.Combine(AppFiles.PluginsPath, "run", pluginId.ToString());

    public static string BrokerFor(Ulid pluginId) => EndpointFor(pluginId, "broker");

    public static string HostFor(Ulid pluginId) => EndpointFor(pluginId, "host");

    private static string EndpointFor(Ulid pluginId, string role) =>
        Software.IsWindows
            ? $"NoMercy.Plugin.{pluginId}.{role}"
            : Path.Combine(RunDirectoryFor(pluginId), $"{role}.sock");
}
