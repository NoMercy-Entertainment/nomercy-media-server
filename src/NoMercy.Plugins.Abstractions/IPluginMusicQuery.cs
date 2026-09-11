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

    /// <summary>
    /// The DJ measurements for the given tracks, for the ones that have an Ok
    /// row.
    /// <para>
    /// A separate call from <see cref="GetAnalysisAsync" /> for the reason
    /// <see cref="PluginTrackDjAnalysis" /> is a separate record: most tracks
    /// have no DJ row for most of a library's life, and a plugin reading the
    /// base analysis should not pay for the heavier DJ measurements it did not
    /// ask for. A track with no DJ row, or one that is not Ok, is absent from
    /// the result rather than returned with empty lists.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<PluginTrackDjAnalysis>> GetDjAnalysisAsync(
        IReadOnlyList<Guid> trackIds,
        CancellationToken ct = default
    );

    /// <summary>
    /// The stem files registered for the given tracks, of every kind and
    /// coverage. A track with no stems yet contributes nothing to the result.
    /// </summary>
    Task<IReadOnlyList<PluginTrackStem>> GetStemsAsync(
        IReadOnlyList<Guid> trackIds,
        CancellationToken ct = default
    );

    /// <summary>
    /// Tracks in the named library that have a base analysis verdict but no DJ
    /// row current with <paramref name="djAnalyzerVersion" /> — the automix
    /// sweep's worklist. <paramref name="take" /> is clamped by the host, the
    /// same way <see cref="GetTracksAsync" /> clamps it. A library id that
    /// does not parse yields an empty result rather than a throw.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetTracksNeedingDjAnalysisAsync(
        string libraryId,
        int djAnalyzerVersion,
        int skip = 0,
        int take = 500,
        CancellationToken ct = default
    );

    /// <summary>
    /// Tracks in the named library whose DJ analysis is Ok but are still
    /// missing a stem file the given <paramref name="policy" /> calls for at
    /// <paramref name="producerVersion" />. <see cref="PluginStemPolicy.OnDemand" />
    /// always answers empty without touching the database — under that policy
    /// there is no sweep to drive. <paramref name="take" /> is clamped the same
    /// way <see cref="GetTracksAsync" /> clamps it, and a library id that does
    /// not parse yields an empty result rather than a throw.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetTracksMissingStemsAsync(
        string libraryId,
        string producerVersion,
        PluginStemPolicy policy,
        int skip = 0,
        int take = 500,
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
/// analysis is normal, not a defect — the detectors are independent, and one
/// finding nothing leaves its members null.
/// </summary>
/// <param name="Bpm">
/// Present whenever the tempo detector produced one, however unsure it was.
/// Read it together with <paramref name="BpmConfidence" />.
/// </param>
/// <param name="BpmConfidence">
/// 0..1, how far to trust <paramref name="Bpm" />; 0 means two detector
/// passes disagreed outright. Null when only one pass ran, so no comparison
/// was possible.
/// </param>
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
