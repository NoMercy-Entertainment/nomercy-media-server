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

namespace NoMercy.MediaProcessing.AudioAnalysis;

/// <summary>
/// The queries the automix plugin's sweep runs to find work: tracks that need
/// a DJ analysis row and tracks that need stem files. Kept beside
/// <see cref="AudioAnalysisQueries" /> for the same reason that one exists in
/// one place — a test can assert the plan rather than assert a copy of it.
/// </summary>
public static class DjAnalysisQueries
{
    /// <summary>
    /// Tracks in the named library that have a base analysis verdict but no
    /// current DJ row: no row at all, a row from an older DJ analyzer, a row
    /// computed from a base analysis that has since moved on, or a row a run
    /// left <see cref="AudioAnalysisState.Pending" /> without finishing.
    /// <para>
    /// A track whose base analysis is not <see cref="AudioAnalysisState.Ok" />
    /// (missing, pending or failed) is never returned — the DJ analyzer needs
    /// the tempo and beat grid the base analysis produces, so there is nothing
    /// for it to work from yet.
    /// </para>
    /// </summary>
    public static IQueryable<Guid> TracksNeedingDjAnalysis(
        MediaContext context,
        Ulid libraryId,
        int djAnalyzerVersion
    )
    {
        return context
            .LibraryTrack.AsNoTracking()
            .Where(libraryTrack => libraryTrack.LibraryId == libraryId)
            .Select(libraryTrack => libraryTrack.TrackId)
            .Distinct()
            .Where(trackId =>
                context.TrackAudioAnalysis.Any(analysis =>
                    analysis.TrackId == trackId && analysis.State == AudioAnalysisState.Ok
                )
                && !context.TrackDjAnalysis.Any(dj =>
                    dj.TrackId == trackId
                    && dj.DjAnalyzerVersion == djAnalyzerVersion
                    && dj.State != AudioAnalysisState.Pending
                    && context.TrackAudioAnalysis.Any(analysis =>
                        analysis.TrackId == trackId
                        && analysis.AnalyzerVersion == dj.BaseAnalyzerVersion
                    )
                )
            )
            .OrderBy(trackId => trackId);
    }

    /// <summary>
    /// Tracks in the named library whose DJ analysis is
    /// <see cref="AudioAnalysisState.Ok" /> but are still missing at least one
    /// of the <paramref name="required" /> stem windows at
    /// <paramref name="producerVersion" />.
    /// <para>
    /// Checked against the "vocals" kind only: the splitter always produces
    /// both stems of the two-stem set together, so one kind stands for both
    /// and keeps the query from doubling its work for nothing it would learn.
    /// A <see cref="StemCoverage.Full" /> row satisfies every required
    /// coverage, since it contains whatever a windowed row would have held.
    /// </para>
    /// </summary>
    public static IQueryable<Guid> TracksMissingStems(
        MediaContext context,
        Ulid libraryId,
        string producerVersion,
        StemCoverage[] required
    )
    {
        int requiredCount = required.Length;

        return context
            .LibraryTrack.AsNoTracking()
            .Where(libraryTrack => libraryTrack.LibraryId == libraryId)
            .Select(libraryTrack => libraryTrack.TrackId)
            .Distinct()
            .Where(trackId =>
                context.TrackDjAnalysis.Any(dj =>
                    dj.TrackId == trackId && dj.State == AudioAnalysisState.Ok
                )
                && !(
                    // A Full stem covers every required window by itself.
                    context.TrackStems.Any(stem =>
                        stem.TrackId == trackId
                        && stem.Kind == "vocals"
                        && stem.ProducerVersion == producerVersion
                        && stem.Coverage == StemCoverage.Full
                    )
                    || context
                        .TrackStems.Where(stem =>
                            stem.TrackId == trackId
                            && stem.Kind == "vocals"
                            && stem.ProducerVersion == producerVersion
                            && required.Contains(stem.Coverage)
                        )
                        .Select(stem => stem.Coverage)
                        .Distinct()
                        .Count() == requiredCount
                )
            )
            .OrderBy(trackId => trackId);
    }
}
