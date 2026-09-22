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

using NoMercy.PluginSdk.Ipc;

namespace NoMercy.PluginHost;

/// <summary>
/// What the server told this process about itself, read once at startup.
/// <para>
/// A missing variable is a refusal naming that variable, not a crash. The
/// process cannot ask anybody what it is: everything it knows arrives this
/// way, so "which one was empty" is the whole diagnosis.
/// </para>
/// </summary>
public sealed record PluginHostLaunch(
    Ulid PluginId,
    string AssemblyPath,
    string DataFolder,
    string BrokerEndpoint,
    string HostEndpoint,
    string Token
)
{
    private static readonly string[] RequiredKeys =
    [
        PluginChannelEnvironment.PluginId,
        PluginChannelEnvironment.AssemblyPath,
        PluginChannelEnvironment.DataFolder,
        PluginChannelEnvironment.BrokerEndpoint,
        PluginChannelEnvironment.HostEndpoint,
        PluginChannelEnvironment.Token,
    ];

    public static bool TryRead(
        IReadOnlyDictionary<string, string?> environment,
        out PluginHostLaunch? launch,
        out WireRefusal? refusal
    )
    {
        launch = null;
        refusal = null;

        foreach (string key in RequiredKeys)
        {
            if (!string.IsNullOrWhiteSpace(environment.GetValueOrDefault(key)))
                continue;

            refusal = Missing(key);
            return false;
        }

        if (!Ulid.TryParse(environment[PluginChannelEnvironment.PluginId], out Ulid pluginId))
        {
            refusal = Missing(PluginChannelEnvironment.PluginId);
            return false;
        }

        launch = new(
            pluginId,
            environment[PluginChannelEnvironment.AssemblyPath]!,
            environment[PluginChannelEnvironment.DataFolder]!,
            environment[PluginChannelEnvironment.BrokerEndpoint]!,
            environment[PluginChannelEnvironment.HostEndpoint]!,
            environment[PluginChannelEnvironment.Token]!
        );

        return true;
    }

    private static WireRefusal Missing(string key) =>
        new(
            "PLUGIN_HOST_UNAVAILABLE",
            "unknown plugin",
            "A plugin process started without the details it needs to run.",
            $"The launch variable {key} was empty, so the process cannot reach the server or find the plugin.",
            "Restart the plugin from its health page; the server sets these variables. Docs: /nomercy-plugins/handbook/runtime-and-isolation",
            "blocked"
        );
}
