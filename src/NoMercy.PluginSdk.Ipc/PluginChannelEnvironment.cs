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

namespace NoMercy.PluginSdk.Ipc;

/// <summary>
/// What the server tells a plugin process about itself, as environment.
/// <para>
/// Passed rather than discovered: a process that worked out its own identity
/// could be pointed at another plugin's data by anything that could set a
/// working directory.
/// </para>
/// </summary>
public static class PluginChannelEnvironment
{
    public const string PluginId = "NOMERCY_PLUGIN_ID";
    public const string AssemblyPath = "NOMERCY_PLUGIN_ASSEMBLY";
    public const string DataFolder = "NOMERCY_PLUGIN_DATA";
    public const string BrokerEndpoint = "NOMERCY_PLUGIN_BROKER";
    public const string HostEndpoint = "NOMERCY_PLUGIN_HOST_ENDPOINT";
    public const string Token = "NOMERCY_PLUGIN_TOKEN";
    public const string MemoryQuotaBytes = "NOMERCY_PLUGIN_QUOTA_MEMORY";
    public const string CpuQuotaPercent = "NOMERCY_PLUGIN_QUOTA_CPU";
    public const string SeccompDescriptor = "NOMERCY_PLUGIN_SECCOMP";

    public const string TokenHeader = "x-nomercy-plugin-token";
}
