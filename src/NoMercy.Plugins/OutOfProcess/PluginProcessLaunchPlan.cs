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

using System.Security.Cryptography;
using NoMercy.PluginSdk.Ipc;
using NoMercy.PluginSdk.Quotas;

namespace NoMercy.PluginSdk.OutOfProcess;

/// <summary>
/// Everything the child process needs to be told, worked out before anything
/// is spawned.
/// <para>
/// Separate from the launching so it can be asserted without starting a
/// process. What is in this dictionary is the whole trust relationship: get
/// the token wrong and every call the plugin makes is refused, get the
/// endpoint wrong and it never connects at all, and both look identical from
/// the outside — a plugin that does nothing.
/// </para>
/// </summary>
public sealed record PluginProcessLaunchPlan(
    Ulid PluginId,
    string AssemblyPath,
    string DataFolder,
    string Token,
    IReadOnlyDictionary<string, string> Environment
)
{
    /// <summary>
    /// A fresh token per launch, never a stored one.
    /// <para>
    /// It only has to outlive the process it belongs to. A token that
    /// survived a restart would still open the channel of a plugin that had
    /// already been shut down for abusing it.
    /// </para>
    /// </summary>
    public static PluginProcessLaunchPlan For(
        Ulid pluginId,
        string assemblyPath,
        string dataFolder,
        PluginQuota? quota = null
    )
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        Dictionary<string, string> environment = new()
        {
            [PluginChannelEnvironment.PluginId] = pluginId.ToString(),
            [PluginChannelEnvironment.AssemblyPath] = assemblyPath,
            [PluginChannelEnvironment.DataFolder] = dataFolder,
            [PluginChannelEnvironment.BrokerEndpoint] = PluginChannelEndpoints.BrokerFor(pluginId),
            [PluginChannelEnvironment.HostEndpoint] = PluginChannelEndpoints.HostFor(pluginId),
            [PluginChannelEnvironment.Token] = token,
        };

        // Only when there is one. An empty variable reads to the child as a
        // quota of zero, which is a plugin that may use nothing rather than
        // one nobody limited.
        if (quota is not null)
        {
            environment[PluginChannelEnvironment.MemoryQuotaBytes] = quota.MemoryBytes.ToString();
            environment[PluginChannelEnvironment.CpuQuotaPercent] = quota.CpuPercent.ToString("0");
        }

        return new PluginProcessLaunchPlan(pluginId, assemblyPath, dataFolder, token, environment);
    }
}
