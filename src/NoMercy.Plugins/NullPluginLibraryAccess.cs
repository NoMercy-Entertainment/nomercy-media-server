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

namespace NoMercy.PluginSdk;

/// <summary>
/// The library as seen by a plugin host built without database access — a test
/// harness, or the manager constructed directly rather than through DI.
/// <para>
/// Empty rather than throwing. A plugin asking what the library holds and being
/// told "nothing" behaves correctly; one that takes an exception on a read does
/// not, and this project cannot reference the database without making the EF
/// model plugin ABI, which is the thing the query contract exists to avoid.
/// </para>
/// </summary>
public sealed class NullPluginLibraryQuery : IPluginLibraryQuery
{
    public Task<IReadOnlyList<PluginLibrary>> GetLibrariesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PluginLibrary>>([]);

    public Task<IReadOnlyList<PluginLibraryShow>> GetShowsAsync(
        string? libraryId = null,
        CancellationToken ct = default
    ) => Task.FromResult<IReadOnlyList<PluginLibraryShow>>([]);

    public Task<IReadOnlyList<PluginLibraryMovie>> GetMoviesAsync(
        string? libraryId = null,
        CancellationToken ct = default
    ) => Task.FromResult<IReadOnlyList<PluginLibraryMovie>>([]);

    public Task<IReadOnlyList<PluginLibraryEpisode>> GetEpisodesAsync(
        int showId,
        CancellationToken ct = default
    ) => Task.FromResult<IReadOnlyList<PluginLibraryEpisode>>([]);

    public Task<IReadOnlyList<PluginLibraryFile>> GetShowFilesAsync(
        int showId,
        CancellationToken ct = default
    ) => Task.FromResult<IReadOnlyList<PluginLibraryFile>>([]);
}

/// <summary>
/// No writer for anyone. The safe default: a host that cannot resolve the
/// storage facade must not hand out the ability to delete media.
/// </summary>
public sealed class NullPluginLibraryWriterFactory : IPluginLibraryWriterFactory
{
    public IPluginLibraryWriter? CreateFor(Ulid pluginId) => null;
}
