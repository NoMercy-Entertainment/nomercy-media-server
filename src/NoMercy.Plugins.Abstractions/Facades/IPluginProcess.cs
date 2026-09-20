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

namespace NoMercy.Plugins.Abstractions;

/// <summary>
/// Child processes, for the binaries a plugin genuinely needs and the server
/// does not wrap.
/// <para>
/// The binary is named, never a shell line: there is no shell here, so nothing a
/// plugin builds from a filename can turn into a second command. A plugin that
/// never declared <c>process.spawn</c> refuses with
/// <see cref="PluginRefusalCodes.ProcessSpawnUndeclared" />.
/// </para>
/// <para>
/// Every process a plugin starts is killed when the plugin stops. A plugin that
/// crashed used to leave its children running until the owner rebooted.
/// </para>
/// </summary>
public interface IPluginProcess
{
    /// <summary>
    /// Starts one of the binaries the manifest named.
    /// <para>
    /// <paramref name="binary" /> resolves to the exact file the owner approved
    /// on the permissions page, never through a PATH search. A plugin declaring
    /// <c>ffmpeg</c> would otherwise run whichever ffmpeg came first on PATH,
    /// which is not the one the owner read.
    /// </para>
    /// <para>
    /// <paramref name="environment" /> is where a credential belongs. An
    /// argument is visible to every other user on the machine through the
    /// process list, so a key passed as an argument leaks locally whatever the
    /// plugin does with it afterwards.
    /// </para>
    /// </summary>
    Task<IPluginProcessHandle> SpawnAsync(
        string binary,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null,
        string? workingDirectory = null,
        CancellationToken ct = default
    );

    /// <summary>Every child this plugin currently has running, which is what the health page shows.</summary>
    IReadOnlyList<IPluginProcessHandle> Running { get; }
}
