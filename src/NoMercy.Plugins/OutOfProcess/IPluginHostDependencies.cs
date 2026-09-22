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

namespace NoMercy.PluginSdk.OutOfProcess;

/// <summary>
/// Where one plugin's files are, and whether it may start anything.
/// <para>
/// Named for files rather than placement: <c>PluginPlacement</c> is where a
/// plugin draws on a screen, and the two were one word apart.
/// </para>
/// </summary>
public sealed record PluginFileLocation(string AssemblyPath, string DataFolder, bool AllowsSpawn);

/// <summary>Answers where a plugin's files are, so the launcher does not go looking.</summary>
public interface IPluginAssemblyLocation
{
    PluginFileLocation For(Ulid pluginId);
}

/// <summary>
/// One broker per plugin, built with that plugin's own facades.
/// <para>
/// A shared broker would have to trust the plugin id on every call, and a
/// compromised child could borrow another plugin's capabilities by typing its
/// id.
/// </para>
/// </summary>
public interface IPluginBrokerFactory
{
    IPluginBrokerService For(Ulid pluginId);
}
