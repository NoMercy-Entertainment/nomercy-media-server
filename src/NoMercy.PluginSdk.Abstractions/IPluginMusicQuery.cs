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
    /// The given tracks, for the ones the host knows and can play. A track
    /// that is unknown or not playable is absent from the result rather than
    /// returned empty. At most as many ids are read as
    /// <see cref="GetTracksAsync" /> returns in one page.
    /// <para>
    /// For a plugin that holds an id and nothing else, such as the one
    /// <see cref="PluginLyricsRequested" /> carries: one call gives it the
    /// length, the title and the artist, exactly as a page of
    /// <see cref="GetTracksAsync" /> would show them. A track that is in more
    /// than one library comes back once per library, as it does there.
    /// Ordered by track id.
    /// </para>
    /// <para>
    /// Default-implemented so an implementer written against an older ABI
    /// keeps compiling; that fallback knows no track and answers empty.
    /// Added in ABI 12.8.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<PluginTrack>> GetTracksByIdAsync(
        IReadOnlyList<Guid> trackIds,
        CancellationToken ct = default
    ) => Task.FromResult<IReadOnlyList<PluginTrack>>([]);

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

    /// <summary>
    /// The DJ rows in the named library that a plugin marked failed at
    /// <paramref name="djAnalyzerVersion" /> — exactly the tracks
    /// <see cref="GetTracksNeedingDjAnalysisAsync" /> leaves out. Ok and
    /// Pending rows, and rows at any other version, are not returned. Paged
    /// and ordered by track id, with <paramref name="take" /> clamped the same
    /// way <see cref="GetTracksAsync" /> clamps it; a library id that does not
    /// parse yields an empty result rather than a throw. Releasing a track for
    /// another attempt is <c>IPluginMusicAnalysisWriter.DeleteDjAnalysisAsync</c>.
    /// </summary>
    Task<IReadOnlyList<PluginTrackDjFailure>> GetFailedDjAnalysisAsync(
        string libraryId,
        int djAnalyzerVersion,
        int skip = 0,
        int take = 500,
        CancellationToken ct = default
    );
}

/// <param name="Album">
/// The name of one album the track is on. <see cref="Albums" /> lists every
/// album, this one first.
/// </param>
/// <param name="Artist">
/// The name of one artist of the track. <see cref="Artists" /> lists every
/// artist, this one first.
/// </param>
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
)
{
    /// <summary>
    /// The names of the track's genres, in alphabetical order. The track's
    /// own; where it has none, those of its albums. Empty when neither has
    /// one, or when the server says nothing about it; never null.
    /// <para>
    /// This and the members below are members rather than positional
    /// parameters, so a plugin compiled against the older shape still
    /// constructs and still reads the eight it knows. Added in ABI 12.15.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Genres
    {
        get;
        init => field = value ?? [];
    } = [];

    /// <summary>
    /// The year the track was released: its own date, or else the year of the
    /// album <see cref="Album" /> names. Null when neither is known. Added in
    /// ABI 12.15.
    /// </summary>
    public int? Year { get; init; }

    /// <summary>
    /// The id of the album <see cref="Album" /> names, so two tracks can be
    /// told to be neighbours on one album. Null when the track is on none.
    /// Added in ABI 12.15.
    /// </summary>
    public Guid? AlbumId { get; init; }

    /// <summary>
    /// Every album the track is on, the one <see cref="Album" /> names first
    /// and the rest by name. Empty when the track is on none, or when the
    /// server says nothing about it; never null. Added in ABI 12.15.
    /// </summary>
    public IReadOnlyList<PluginTrackAlbum> Albums
    {
        get;
        init => field = value ?? [];
    } = [];

    /// <summary>
    /// Every artist of the track, the one <see cref="Artist" /> names first
    /// and the rest by name: the server keeps no order of its own for them.
    /// Empty when the track has none, or when the server says nothing about
    /// it; never null. Added in ABI 12.15.
    /// </summary>
    public IReadOnlyList<PluginTrackArtist> Artists
    {
        get;
        init => field = value ?? [];
    } = [];

    /// <summary>
    /// Value equality, with the three lists compared by their items. The
    /// generated equality would compare them by reference, so two reads of one
    /// unchanged track would stop being equal, and a plugin built before 12.15
    /// that spots changes with <c>==</c>, a set or a dictionary key would see
    /// every track change on every read.
    /// </summary>
    public virtual bool Equals(PluginTrack? other) =>
        ReferenceEquals(this, other)
        || (
            other is not null
            && EqualityContract == other.EqualityContract
            && Id == other.Id
            && Title == other.Title
            && Album == other.Album
            && Artist == other.Artist
            && TrackNumber == other.TrackNumber
            && DiscNumber == other.DiscNumber
            && Nullable.Equals(DurationSeconds, other.DurationSeconds)
            && LibraryId == other.LibraryId
            && Year == other.Year
            && AlbumId == other.AlbumId
            && Genres.SequenceEqual(other.Genres)
            && Albums.SequenceEqual(other.Albums)
            && Artists.SequenceEqual(other.Artists)
        );

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(EqualityContract);
        hash.Add(Id);
        hash.Add(Title);
        hash.Add(Album);
        hash.Add(Artist);
        hash.Add(TrackNumber);
        hash.Add(DiscNumber);
        hash.Add(DurationSeconds);
        hash.Add(LibraryId);
        hash.Add(Year);
        hash.Add(AlbumId);

        foreach (string genre in Genres)
            hash.Add(genre);
        foreach (PluginTrackAlbum album in Albums)
            hash.Add(album);
        foreach (PluginTrackArtist artist in Artists)
            hash.Add(artist);

        return hash.ToHashCode();
    }
}

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
