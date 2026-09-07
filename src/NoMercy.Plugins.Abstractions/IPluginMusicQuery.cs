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
/// Reading the music library, including what analysis measured about it.
/// <para>
/// <see cref="IPluginLibraryQuery" /> covers libraries, shows, movies, episodes
/// and files. It has never covered music, so a plugin working on a music
/// library had nothing to ask. This is that surface, and it follows the same
/// rules: every type here is owned by this assembly, never the EF model, so a
/// migration is a change in the host and not a break in every installed plugin.
/// </para>
/// <para>
/// Read-only by construction, so it needs no capability — the same reasoning
/// <see cref="IPluginLibraryQuery" /> gives for itself. Playing something is a
/// different question, already answered by <see cref="PluginCapability.Player" />
/// and <see cref="PluginGrantKind.PlayerSource" />.
/// </para>
/// </summary>
public interface IPluginMusicQuery
{
    /// <summary>
    /// Tracks, optionally narrowed to one library.
    /// <para>
    /// Paged, unlike the video methods on <see cref="IPluginLibraryQuery" />. A
    /// show count is small and a track count is not, so returning the lot would
    /// hand a plugin a list it cannot hold. <paramref name="take" /> is capped
    /// by the host.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<PluginTrack>> GetTracksAsync(
        string? libraryId = null,
        int skip = 0,
        int take = 500,
        CancellationToken ct = default
    );

    /// <summary>
    /// What analysis measured, for the tracks that have it.
    /// <para>
    /// A second call rather than a member of <see cref="PluginTrack" />: most
    /// rows have no analysis for most of a library's life, and a plugin listing
    /// a library should not pay for measurements it will not read. A track with
    /// no analysis is absent from the result rather than returned empty, so the
    /// gap is explicit.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<PluginTrackAudioAnalysis>> GetAnalysisAsync(
        IReadOnlyList<Guid> trackIds,
        CancellationToken ct = default
    );
}

/// <param name="DurationSeconds">Null when the library never recorded one.</param>
public record PluginTrack(
    Guid Id,
    string Title,
    string? Album,
    string? Artist,
    int? TrackNumber,
    int? DiscNumber,
    double? DurationSeconds,
    string LibraryId
);

/// <summary>
/// One track's measurements. Every member is nullable because a partial
/// analysis is normal, not a defect — the detectors are independent, and tempo
/// in particular is withheld while it cannot be trusted.
/// </summary>
/// <param name="KeyName">As the detector named it: "C", "F#", "Am".</param>
/// <param name="KeyCamelot">The same key as a Camelot code: "8A".</param>
/// <param name="Energy">
/// A 0..1 judgment derived from <paramref name="IntegratedLufs" /> and
/// <paramref name="SpectralCentroid" />, not a measurement. Both inputs are
/// returned so a consumer that disagrees can recompute.
/// </param>
/// <param name="IntroEndMs">
/// End of leading silence. A trim point, not a musical phrase boundary.
/// </param>
/// <param name="OutroStartMs">Start of trailing silence, as above.</param>
/// <param name="AnalyzerVersion">
/// Which analyzer produced this. A plugin holding cached results can compare it
/// rather than assuming what it stored is still current.
/// </param>
public record PluginTrackAudioAnalysis(
    Guid TrackId,
    double? Bpm,
    double? BpmConfidence,
    int? BeatOffsetMs,
    double? BeatIntervalMs,
    string? KeyName,
    string? KeyCamelot,
    double? KeyConfidence,
    double? IntegratedLufs,
    double? TruePeakDb,
    double? LoudnessRange,
    double? Energy,
    double? SpectralCentroid,
    int? IntroEndMs,
    int? OutroStartMs,
    int AnalyzerVersion
);
