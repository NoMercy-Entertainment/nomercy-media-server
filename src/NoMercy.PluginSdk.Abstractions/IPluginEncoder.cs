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
/// Asking the server to take a finished file into a library.
///
/// <para>
/// There was no way to ask, so a plugin reached the server's own types by name
/// through the host container and drove them with reflection.
/// Between 22 and 24 August 2026 that construction broke four separate times and
/// every break looked identical from the outside: the plugin simply never
/// encoded anything, for days, while all of its tests stayed green. An overload
/// was added and <c>GetMethod</c> threw; a property changed from text to
/// <c>Ulid</c> and <c>SetValue</c> threw; a method started returning a
/// <c>Task</c> and the plugin walked it as a list and found nothing; two
/// interfaces shared a name and the unregistered one resolved to null. A
/// compiler catches none of those, and none of them produce an error an owner
/// can act on.
/// </para>
///
/// <para>
/// Ids are text in both directions, as the rest of this contract already spells
/// them, so a plugin never has to know <c>Ulid</c> exists.
/// </para>
/// </summary>
public interface IPluginEncoder
{
    /// <summary>
    /// Take a file the plugin has staged into a library.
    /// </summary>
    /// <param name="file">An absolute path to a video the plugin has finished writing.</param>
    /// <param name="libraryId">The library it belongs in - <see cref="PluginLibraryShow.LibraryId" />.</param>
    /// <param name="mediaId">
    /// The server's own id for the episode or movie this file is, from
    /// <see cref="PluginLibraryEpisode.Id" /> or <see cref="PluginLibraryMovie.Id" />.
    /// <para>
    /// Null asks the server to work it out from the filename, which is what it
    /// did before this existed: a text search on whatever a parser read out of
    /// the name. That is a fair guess for a folder a person just pointed at and
    /// a guess a plugin holding the episode never needed to make - it resolved
    /// nothing, the encode registered against no row, the queue counter moved
    /// and the library stayed empty.
    /// </para>
    /// </param>
    /// <param name="presetId">Null keeps the library's own presets.</param>
    Task<PluginEncodeResult> EncodeAsync(
        string file,
        string libraryId,
        string? mediaId = null,
        string? presetId = null,
        CancellationToken ct = default
    );
}

/// <summary>
/// What became of the ask.
///
/// <para>
/// The refusal matters as much as the job id. Every reason below has really
/// happened, and each is something the owner can act on once it is said out
/// loud instead of appearing as an encode that never runs.
/// </para>
/// </summary>
/// <param name="JobId">The queued job, to be given to <see cref="IPluginJobs" />. Null when refused.</param>
/// <param name="Refusal">Why not, in words the owner can act on. Null when accepted.</param>
public sealed record PluginEncodeResult(string? JobId, string? Refusal)
{
    public bool Accepted => JobId is not null;

    public static PluginEncodeResult Queued(string jobId)
    {
        return new(jobId, null);
    }

    public static PluginEncodeResult Refused(string reason)
    {
        return new(null, reason);
    }
}
