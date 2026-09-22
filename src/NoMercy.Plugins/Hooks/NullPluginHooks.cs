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

using NoMercy.NmSystem.Dto;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginSdk.Hooks;

/// <summary>
/// A server with no plugin platform at all — which is what a test harness
/// constructing a manager by hand is.
/// <para>
/// The default for the hook parameters, so a test that has nothing to do with
/// plugins does not have to know they exist. Every path that runs for a real
/// user is handed the real dispatcher, by the container or by the job that built
/// the manager, so this answers for nobody in production.
/// </para>
/// </summary>
public sealed class NullPluginMediaSourceProvider : IPluginMediaSourceProvider
{
    public static NullPluginMediaSourceProvider Instance { get; } = new();

    public Task<IReadOnlyList<MediaFolderExtend>> ScanAsync(
        string path,
        CancellationToken ct = default
    ) => Task.FromResult<IReadOnlyList<MediaFolderExtend>>([]);
}

/// <inheritdoc cref="NullPluginMediaSourceProvider" />
public sealed class NullPluginMetadataResolver : IPluginMetadataResolver
{
    public static NullPluginMetadataResolver Instance { get; } = new();

    public Task<MediaMetadata?> ResolveAsync(
        string title,
        MediaType type,
        CancellationToken ct = default
    ) => Task.FromResult<MediaMetadata?>(null);
}
