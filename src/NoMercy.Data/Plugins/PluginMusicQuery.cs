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

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NoMercy.Data.Music;
using NoMercy.Database;
using NoMercy.Database.Models.Music;
using NoMercy.MediaProcessing.AudioAnalysis;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Data.Plugins;

/// <summary>
/// Music and its analysis, translated for plugins.
/// <para>
/// The same contract that keeps <see cref="PluginLibraryQuery" /> honest: every
/// method projects into a record owned by the plugin abstractions, so a
/// migration is a change here and nowhere else. Read-only, every query
/// <c>AsNoTracking</c>.
/// </para>
/// </summary>
public class PluginMusicQuery(
    IDbContextFactory<MediaContext> contextFactory,
    ILogger<PluginMusicQuery> logger
) : IPluginMusicQuery
{
    /// <summary>
    /// The most tracks one call will return, whatever the caller asked for. A
    /// plugin naming a huge page would otherwise pull an entire library into
    /// memory in one hop.
    /// </summary>
    private const int MaxPageSize = 1000;

    public async Task<IReadOnlyList<PluginTrack>> GetTracksAsync(
        string? libraryId = null,
        int skip = 0,
        int take = 500,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        IQueryable<LibraryTrack> libraryTracks = context.LibraryTrack.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(libraryId) && Ulid.TryParse(libraryId, out Ulid parsed))
        {
            libraryTracks = libraryTracks.Where(libraryTrack => libraryTrack.LibraryId == parsed);
        }

        List<TrackRow> rows = await libraryTracks
            // Ordered because a page without one is a page that can repeat or
            // skip rows between calls.
            .OrderBy(libraryTrack => libraryTrack.TrackId)
            .Skip(Math.Max(0, skip))
            .Take(Math.Clamp(take, 1, MaxPageSize))
            .Select(libraryTrack => new TrackRow(
                libraryTrack.Track.Id,
                libraryTrack.Track.Name,
                libraryTrack
                    .Track.AlbumTrack.Select(albumTrack => albumTrack.Album.Name)
                    .FirstOrDefault(),
                libraryTrack
                    .Track.ArtistTrack.Select(artistTrack => artistTrack.Artist.Name)
                    .FirstOrDefault(),
                libraryTrack.Track.TrackNumber,
                libraryTrack.Track.DiscNumber,
                libraryTrack.Track.Duration,
                libraryTrack.LibraryId.ToString()
            ))
            .ToListAsync(ct);

        return rows.Select(row => new PluginTrack(
                row.Id,
                row.Title,
                row.Album,
                row.Artist,
                row.TrackNumber,
                row.DiscNumber,
                ParseDurationSeconds(row.Duration),
                row.LibraryId
            ))
            .ToList();
    }

    public async Task<IReadOnlyList<PluginTrackAudioAnalysis>> GetAnalysisAsync(
        IReadOnlyList<Guid> trackIds,
        CancellationToken ct = default
    )
    {
        if (trackIds.Count == 0)
        {
            return [];
        }

        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        Guid[] ids = trackIds.Distinct().Take(MaxPageSize).ToArray();

        return await context
            .TrackAudioAnalysis.AsNoTracking()
            // Only rows that actually measured something. A failed or pending
            // row is an absence to the caller, not a set of null readings.
            .Where(analysis =>
                ids.Contains(analysis.TrackId) && analysis.State == AudioAnalysisState.Ok
            )
            .Select(analysis => new PluginTrackAudioAnalysis(
                analysis.TrackId,
                analysis.Bpm,
                analysis.BpmConfidence,
                analysis.BeatOffsetMs,
                analysis.BeatIntervalMs,
                analysis.KeyName,
                analysis.KeyCamelot,
                analysis.KeyConfidence,
                analysis.IntegratedLufs,
                analysis.TruePeakDb,
                analysis.LoudnessRange,
                analysis.Energy,
                analysis.SpectralCentroid,
                analysis.IntroEndMs,
                analysis.OutroStartMs,
                analysis.AnalyzerVersion
            ))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<PluginTrackDjAnalysis>> GetDjAnalysisAsync(
        IReadOnlyList<Guid> trackIds,
        CancellationToken ct = default
    )
    {
        if (trackIds.Count == 0)
        {
            return [];
        }

        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        Guid[] ids = trackIds.Distinct().Take(MaxPageSize).ToArray();

        List<DjAnalysisRow> rows = await context
            .TrackDjAnalysis.AsNoTracking()
            .Where(dj => ids.Contains(dj.TrackId) && dj.State == AudioAnalysisState.Ok)
            .Select(dj => new DjAnalysisRow(
                dj.TrackId,
                dj.DjAnalyzerVersion,
                dj.BaseAnalyzerVersion,
                dj.DownbeatIndex,
                dj.BeatsPerBar,
                dj.PhraseLengthBars,
                dj.PhraseStartsMs,
                dj.VocalRegionsMs,
                dj.BarEnergy,
                dj.CuePoints,
                dj.Chords
            ))
            .ToListAsync(ct);

        return rows.Select(row => new PluginTrackDjAnalysis(
                row.TrackId,
                row.DjAnalyzerVersion,
                row.BaseAnalyzerVersion,
                row.DownbeatIndex,
                row.BeatsPerBar,
                row.PhraseLengthBars,
                DeserializeJsonList<int>(row.PhraseStartsMs, row.TrackId, "phrase_starts_ms"),
                DeserializeJsonList<int[]>(row.VocalRegionsMs, row.TrackId, "vocal_regions_ms"),
                DeserializeJsonList<double>(row.BarEnergy, row.TrackId, "bar_energy"),
                DeserializeJsonList<CuePointRow>(row.CuePoints, row.TrackId, "cue_points")
                    .Select(cue => new PluginCuePoint(
                        cue.Ms,
                        cue.Type ?? string.Empty,
                        cue.Direction ?? string.Empty,
                        cue.Score
                    ))
                    .ToList(),
                DeserializeJsonList<ChordRow>(row.Chords, row.TrackId, "chords")
                    .Select(chord => new PluginChord(chord.Ms, chord.Chord ?? string.Empty))
                    .ToList()
            ))
            .ToList();
    }

    public async Task<IReadOnlyList<PluginTrackStem>> GetStemsAsync(
        IReadOnlyList<Guid> trackIds,
        CancellationToken ct = default
    )
    {
        if (trackIds.Count == 0)
        {
            return [];
        }

        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        Guid[] ids = trackIds.Distinct().Take(MaxPageSize).ToArray();

        List<StemRow> rows = await context
            .TrackStems.AsNoTracking()
            .Where(stem => ids.Contains(stem.TrackId))
            .Select(stem => new StemRow(
                stem.TrackId,
                stem.Kind,
                stem.Coverage,
                stem.WindowStartMs,
                stem.WindowEndMs,
                stem.Format,
                stem.SampleRate,
                stem.StorageKey,
                stem.ProducerVersion
            ))
            .ToListAsync(ct);

        return rows.Select(row => new PluginTrackStem(
                row.TrackId,
                row.Kind,
                StemCoverageMap.ToPlugin(row.Coverage),
                row.WindowStartMs,
                row.WindowEndMs,
                row.Format,
                row.SampleRate,
                row.StorageKey,
                row.ProducerVersion
            ))
            .ToList();
    }

    public async Task<IReadOnlyList<Guid>> GetTracksNeedingDjAnalysisAsync(
        string libraryId,
        int djAnalyzerVersion,
        int skip = 0,
        int take = 500,
        CancellationToken ct = default
    )
    {
        if (!Ulid.TryParse(libraryId, out Ulid parsedLibraryId))
        {
            return [];
        }

        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        return await DjAnalysisQueries
            .TracksNeedingDjAnalysis(context, parsedLibraryId, djAnalyzerVersion)
            .Skip(Math.Max(0, skip))
            .Take(Math.Clamp(take, 1, MaxPageSize))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetTracksMissingStemsAsync(
        string libraryId,
        string producerVersion,
        PluginStemPolicy policy,
        int skip = 0,
        int take = 500,
        CancellationToken ct = default
    )
    {
        // Nothing to sweep for: a plugin under this policy splits the first
        // time a track is asked for, so there is no worklist to compute.
        if (policy == PluginStemPolicy.OnDemand)
        {
            return [];
        }

        if (!Ulid.TryParse(libraryId, out Ulid parsedLibraryId))
        {
            return [];
        }

        StemCoverage[] required =
            policy == PluginStemPolicy.Full
                ? [StemCoverage.Full]
                : [StemCoverage.MixIn, StemCoverage.MixOut];

        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        return await DjAnalysisQueries
            .TracksMissingStems(context, parsedLibraryId, producerVersion, required)
            .Skip(Math.Max(0, skip))
            .Take(Math.Clamp(take, 1, MaxPageSize))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Deserializes one of <see cref="TrackDjAnalysis" />'s JSON text columns
    /// via <see cref="DjAnalysisJson.TryDeserialize{T}(string?, out Exception?)" />.
    /// A missing or malformed column is never this method's caller's problem
    /// to throw over — it logs what it found (the parse exception too, when
    /// there was one) and hands back an empty list, so one bad row never
    /// takes a whole page of plugin results down with it.
    /// </summary>
    private List<T> DeserializeJsonList<T>(string? json, Guid trackId, string column)
    {
        List<T>? result = DjAnalysisJson.TryDeserialize<T>(json, out Exception? error);
        if (result is not null)
        {
            return result;
        }

        if (error is null)
        {
            logger.LogWarning(
                "Track {TrackId}: {Column} column is empty on an Ok DJ analysis row; treating it as an empty list",
                trackId,
                column
            );
        }
        else
        {
            logger.LogWarning(
                error,
                "Track {TrackId}: malformed {Column} JSON on a DJ analysis row; treating it as an empty list",
                trackId,
                column
            );
        }

        return [];
    }

    /// <summary>
    /// The library stores a duration as ffprobe's "hh:mm:ss" with a leading
    /// "00:" stripped, so a track under an hour reads "mm:ss". Parsed by hand:
    /// <see cref="TimeSpan.TryParse(string?, out TimeSpan)" /> takes two parts
    /// as hours and minutes and would make a 3:45 track last all afternoon.
    /// <para>
    /// <c>internal</c> rather than private: the one duration parser in the
    /// repo, so <see cref="PluginMusicAnalysisWriter" /> reuses it for its
    /// track-duration bound checks instead of copying it.
    /// </para>
    /// </summary>
    internal static double? ParseDurationSeconds(string? duration)
    {
        if (string.IsNullOrWhiteSpace(duration))
        {
            return null;
        }

        string[] parts = duration.Split(':');

        if (parts.Length is < 2 or > 3)
        {
            return null;
        }

        double seconds = 0;
        foreach (string part in parts)
        {
            if (
                !double.TryParse(
                    part,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double value
                )
            )
            {
                return null;
            }

            seconds = seconds * 60 + value;
        }

        return seconds;
    }

    /// <summary>
    /// The shape one page comes off the database in. Duration stays a string
    /// here because SQLite cannot run the conversion; it happens in memory.
    /// </summary>
    private sealed record TrackRow(
        Guid Id,
        string Title,
        string? Album,
        string? Artist,
        int? TrackNumber,
        int? DiscNumber,
        string? Duration,
        string LibraryId
    );

    /// <summary>
    /// One DJ analysis row before its JSON columns are parsed into the typed
    /// lists <see cref="PluginTrackDjAnalysis" /> carries.
    /// </summary>
    private sealed record DjAnalysisRow(
        Guid TrackId,
        int DjAnalyzerVersion,
        int BaseAnalyzerVersion,
        int? DownbeatIndex,
        int BeatsPerBar,
        int PhraseLengthBars,
        string? PhraseStartsMs,
        string? VocalRegionsMs,
        string? BarEnergy,
        string? CuePoints,
        string? Chords
    );

    /// <summary>
    /// One stem row before <see cref="StemCoverage" /> is mapped to
    /// <see cref="PluginStemCoverage" />.
    /// </summary>
    private sealed record StemRow(
        Guid TrackId,
        string Kind,
        StemCoverage Coverage,
        int? WindowStartMs,
        int? WindowEndMs,
        string Format,
        int SampleRate,
        string StorageKey,
        string ProducerVersion
    );
}
