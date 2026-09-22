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

using NoMercy.Plugins.Abstractions;

namespace NoMercy.PluginHost;

/// <summary>
/// Reads of the owner's library.
/// <para>
/// The one facade whose answer is the data rather than a permit. The plugin
/// holds no database handle and never will, so there is nothing to hand it a
/// path to, and a title with an episode count is small enough to cross.
/// </para>
/// <para>
/// Nothing is cached. A library is what the owner is changing while the plugin
/// runs, and an answer remembered from startup is a plugin that cannot see the
/// show its owner just added.
/// </para>
/// </summary>
public sealed class RemoteLibrary(Ulid pluginId, RemoteCall call) : IPluginLibraryQuery
{
    public async Task<IReadOnlyList<PluginLibrary>> GetLibrariesAsync(
        CancellationToken ct = default
    ) =>
        await call.AskAsync<IReadOnlyList<PluginLibrary>>(
            "library",
            nameof(IPluginLibraryQuery.GetLibrariesAsync)
        ) ?? [];

    public async Task<IReadOnlyList<PluginLibraryShow>> GetShowsAsync(
        string? libraryId = null,
        CancellationToken ct = default
    ) =>
        await call.AskAsync<IReadOnlyList<PluginLibraryShow>>(
            "library",
            nameof(IPluginLibraryQuery.GetShowsAsync),
            new { libraryId }
        ) ?? [];

    public async Task<IReadOnlyList<PluginLibraryMovie>> GetMoviesAsync(
        string? libraryId = null,
        CancellationToken ct = default
    ) =>
        await call.AskAsync<IReadOnlyList<PluginLibraryMovie>>(
            "library",
            nameof(IPluginLibraryQuery.GetMoviesAsync),
            new { libraryId }
        ) ?? [];

    public async Task<IReadOnlyList<PluginLibraryEpisode>> GetEpisodesAsync(
        int showId,
        CancellationToken ct = default
    ) =>
        await call.AskAsync<IReadOnlyList<PluginLibraryEpisode>>(
            "library",
            nameof(IPluginLibraryQuery.GetEpisodesAsync),
            new { showId }
        ) ?? [];

    public async Task<IReadOnlyList<PluginLibraryFile>> GetShowFilesAsync(
        int showId,
        CancellationToken ct = default
    ) =>
        await call.AskAsync<IReadOnlyList<PluginLibraryFile>>(
            "library",
            nameof(IPluginLibraryQuery.GetShowFilesAsync),
            new { showId }
        ) ?? [];

    /// <summary>
    /// Offering a file to a library, and watching one for changes, cross the
    /// boundary in their own task. A watch that never fired would look to a
    /// plugin like a library nobody is touching.
    /// </summary>
    public IPluginLibraryImport Import =>
        throw new PluginRefusedException(NotYet(nameof(IPluginLibraryQuery.Import)));

    public IPluginLibraryWatch Watch =>
        throw new PluginRefusedException(NotYet(nameof(IPluginLibraryQuery.Watch)));

    private PluginRefusal NotYet(string member) =>
        new(
            PluginRefusalCodes.HostServicesRemoved,
            pluginId.ToString(),
            $"A plugin in its own process asked for library.{member}.",
            "This server runs the plugin out of process, and that member does not cross the boundary yet.",
            "Run this plugin in the server's own process until it is carried across. Docs: /nomercy-plugins/handbook/runtime-and-isolation",
            PluginRefusalSeverity.Blocked
        );
}
