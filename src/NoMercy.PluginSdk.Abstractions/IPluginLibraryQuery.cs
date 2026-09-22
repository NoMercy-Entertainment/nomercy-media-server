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

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// Reading what the library already holds.
/// <para>
/// A plugin whose job depends on the library — what is missing, what is already
/// there, where it lives — had no sanctioned way to ask. The mechanical answer
/// was to share <c>NoMercy.Database</c> with plugins so they could resolve the
/// EF context, and that answer is wrong: it makes the EF model public plugin
/// ABI, so every migration becomes a potential plugin break. Self-hosted users
/// do not get broken, and that includes by their plugins.
/// </para>
/// <para>
/// So the contract returns types owned by this assembly. The EF model stays
/// free to change, and this interface is the thing that has to stay still.
/// Read-only by construction, which is why it needs no capability: nothing here
/// can alter a user's library. Writing is
/// <see cref="PluginGrantKind.LibraryWrite"/> and a different contract.
/// </para>
/// </summary>
public interface IPluginLibraryQuery
{
    /// <summary>
    /// Offering a finished file to a library. Needs <c>library.import</c>;
    /// reaching it without refuses rather than answering null, the rule this
    /// server sets for every facade it added.
    /// </summary>
    IPluginLibraryImport Import =>
        throw new PluginRefusedException(
            PluginRefusalMessages.CapabilityNotDeclared(
                "unknown plugin",
                PluginCapabilityNames.LibraryImport,
                "The plugin offered a file to a library."
            )
        );

    /// <summary>Being told when a library changes, rather than polling it.</summary>
    IPluginLibraryWatch Watch =>
        throw new PluginRefusedException(
            PluginRefusalMessages.CapabilityNotDeclared(
                "unknown plugin",
                PluginCapabilityNames.LibraryWatch,
                "The plugin subscribed to a library."
            )
        );

    /// <summary>Every library the server has, of every type.</summary>
    Task<IReadOnlyList<PluginLibrary>> GetLibrariesAsync(CancellationToken ct = default);

    /// <summary>Shows, optionally narrowed to one library.</summary>
    Task<IReadOnlyList<PluginLibraryShow>> GetShowsAsync(
        string? libraryId = null,
        CancellationToken ct = default
    );

    /// <summary>Movies, optionally narrowed to one library.</summary>
    Task<IReadOnlyList<PluginLibraryMovie>> GetMoviesAsync(
        string? libraryId = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Every episode of a show, including the ones with no file. A plugin
    /// computing what is missing needs the gaps, so an episode with no file is
    /// returned with <see cref="PluginLibraryEpisode.HasFile"/> false rather
    /// than omitted.
    /// </summary>
    Task<IReadOnlyList<PluginLibraryEpisode>> GetEpisodesAsync(
        int showId,
        CancellationToken ct = default
    );

    /// <summary>The files held against one show.</summary>
    Task<IReadOnlyList<PluginLibraryFile>> GetShowFilesAsync(
        int showId,
        CancellationToken ct = default
    );
}

/// <param name="Id">The library's id, as used by <see cref="PluginGrantKind.LibraryWrite"/>.</param>
/// <param name="Type">movie, tv, anime or music.</param>
public record PluginLibrary(string Id, string Title, string Type);

/// <param name="Id">The provider's show id, which is what the rest of this contract keys on.</param>
/// <param name="Folder">The show's folder relative to its library root, or null when it has none.</param>
public record PluginLibraryShow(
    int Id,
    string Title,
    int? Year,
    string LibraryId,
    string? Folder,
    int EpisodeCount,
    int HaveEpisodeCount
);

public record PluginLibraryMovie(
    int Id,
    string Title,
    int? Year,
    string LibraryId,
    string? Folder,
    bool HasFile
);

/// <param name="HasFile">Whether the library holds a file for this episode. False is the gap a plugin is looking for.</param>
public record PluginLibraryEpisode(
    int ShowId,
    int SeasonNumber,
    int EpisodeNumber,
    string? Title,
    DateTime? AirDate,
    bool HasFile
)
{
    /// <summary>
    /// The server's own id for this episode, which is what an encode registers
    /// its result against.
    /// <para>
    /// Without it a plugin holding exactly the episode it means had no way to
    /// say which row the work belongs to, and the only route to the id was
    /// asking the server to re-identify the file from its name — a text search
    /// on whatever a parser read out of it. That is a fair guess for a folder a
    /// person just pointed at and a guess a plugin never needed to make: the
    /// encode registered against nothing, the queue counter moved, the library
    /// stayed empty, and from outside it looked like an encode still running.
    /// </para>
    /// <para>
    /// A member rather than a positional parameter, so every plugin compiled
    /// against the older shape still constructs. It leads the record at the next
    /// contract bump, where <see cref="PluginLibraryShow"/> and
    /// <see cref="PluginLibraryMovie"/> already carry theirs.
    /// </para>
    /// </summary>
    public int Id { get; init; }
}

/// <param name="Path">Full path, as the server records it.</param>
/// <param name="Quality">The server's own quality label for the file, or empty when it has none.</param>
public record PluginLibraryFile(
    int ShowId,
    int? SeasonNumber,
    int? EpisodeNumber,
    string Path,
    string Quality
);
