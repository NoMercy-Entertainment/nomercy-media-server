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
/// The queries the analysis sweep runs, in one place so a test can assert their
/// plan rather than assert a copy of it.
/// </summary>
public static class AudioAnalysisQueries
{
    private const string MusicLibraryType = "music";

    /// <summary>
    /// The music libraries that want their audio analyzed. The hourly sweep,
    /// the hook that fires when a scan finishes and the dashboard's run-now
    /// button all mean this same set, so none of them spells it out again.
    /// </summary>
    public static IQueryable<Ulid> LibrariesToAnalyze(MediaContext context)
    {
        return context
            .Libraries.AsNoTracking()
            .Where(library => library.AnalyzeAudio && library.Type == MusicLibraryType)
            .Select(library => library.Id);
    }

    /// <summary>
    /// Tracks in the named libraries that carry no verdict from this analyzer
    /// version — no row at all, a row from an older analyzer, or a row left
    /// Pending by a run that did not finish.
    /// </summary>
    public static IQueryable<Guid> TracksNeedingAnalysis(
        MediaContext context,
        IReadOnlyCollection<Ulid> libraryIds,
        int analyzerVersion
    )
    {
        return context
            .LibraryTrack.AsNoTracking()
            .Where(libraryTrack => libraryIds.Contains(libraryTrack.LibraryId))
            .Select(libraryTrack => libraryTrack.TrackId)
            .Distinct()
            .Where(trackId =>
                !context.TrackAudioAnalysis.Any(analysis =>
                    analysis.TrackId == trackId
                    && analysis.AnalyzerVersion == analyzerVersion
                    && analysis.State != AudioAnalysisState.Pending
                )
            );
    }
}
