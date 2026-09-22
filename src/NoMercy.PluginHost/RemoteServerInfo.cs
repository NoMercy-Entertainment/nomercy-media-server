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

namespace NoMercy.PluginHost;

/// <summary>
/// What this server is, as the plugin process may ask.
/// <para>
/// The version and the platform are fixed for the life of the process, so they
/// are asked once. The granted paths are not: the owner can grant a folder
/// while the plugin is running, and a list cached at startup would tell the
/// plugin its newest folder does not exist.
/// </para>
/// </summary>
public sealed class RemoteServerInfo(RemoteCall call) : IPluginServerInfo
{
    private Version? _version;
    private string? _platform;

    public Version Version =>
        _version ??= Version.TryParse(
            call.Ask<string>("server", nameof(IPluginServerInfo.Version)),
            out Version? parsed
        )
            ? parsed
            : new Version(0, 0);

    public string Platform =>
        _platform ??=
            call.Ask<string>("server", nameof(IPluginServerInfo.Platform)) ?? string.Empty;

    public IReadOnlyList<PluginStorageLocation> GrantedPaths =>
        call.Ask<IReadOnlyList<PluginStorageLocation>>(
            "server",
            nameof(IPluginServerInfo.GrantedPaths)
        ) ?? [];

    public async Task<long> FreeSpaceBytesAsync(string folderId, CancellationToken ct = default) =>
        await call.AskAsync<long>(
            "server",
            nameof(IPluginServerInfo.FreeSpaceBytesAsync),
            new { folderId }
        );
}
