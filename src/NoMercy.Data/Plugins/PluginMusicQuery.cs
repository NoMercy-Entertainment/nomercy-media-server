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

using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Database.Models.Music;
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
public class PluginMusicQuery(IDbContextFactory<MediaContext> contextFactory) : IPluginMusicQuery
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

        return await libraryTracks
            // Ordered because a page without one is a page that can repeat or
            // skip rows between calls.
            .OrderBy(libraryTrack => libraryTrack.TrackId)
            .Skip(Math.Max(0, skip))
            .Take(Math.Clamp(take, 1, MaxPageSize))
            .Select(libraryTrack => new PluginTrack(
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
                null,
                libraryTrack.LibraryId.ToString()
            ))
            .ToListAsync(ct);
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
}
