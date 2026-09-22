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
/// Writing to the owner's library, over the channel.
/// <para>
/// Nothing here touches a file. The server owns the library roots and the
/// recycle bin, and a delete the plugin's own process performed would be a
/// delete the broker never saw and never gated.
/// </para>
/// </summary>
public sealed class RemoteLibraryWriter(RemoteCall call) : IPluginLibraryWriter
{
    public async Task<IReadOnlyList<PluginLibrary>> GetWritableLibrariesAsync(
        CancellationToken ct = default
    ) =>
        await call.AskAsync<IReadOnlyList<PluginLibrary>>(
            "libraryWriter",
            nameof(IPluginLibraryWriter.GetWritableLibrariesAsync)
        ) ?? [];

    public Task RecycleAsync(string path, CancellationToken ct = default) =>
        call.TellAsync("libraryWriter", nameof(IPluginLibraryWriter.RecycleAsync), new { path });

    public Task DeleteAsync(string path, CancellationToken ct = default) =>
        call.TellAsync("libraryWriter", nameof(IPluginLibraryWriter.DeleteAsync), new { path });

    public Task MoveAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken ct = default
    ) =>
        call.TellAsync(
            "libraryWriter",
            nameof(IPluginLibraryWriter.MoveAsync),
            new { path = sourcePath, destinationPath }
        );

    public Task<bool> CanWriteAsync(string path, CancellationToken ct = default) =>
        call.AskAsync<bool>(
            "libraryWriter",
            nameof(IPluginLibraryWriter.CanWriteAsync),
            new { path }
        );
}
