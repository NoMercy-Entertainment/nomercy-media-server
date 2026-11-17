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

namespace NoMercy.Encoder.ContentAnalysis.Segments;

/// <summary>
/// The pure stages (2–8) of the intro / credits / end-of-content analysis (#598).
/// No I/O: the caller measures the signals (stage 1) and fetches the picture
/// evidence the candidates ask for between <see cref="FindCandidates"/> and
/// <see cref="Decide"/>.
/// </summary>
public interface ISeasonSegmentAnalyzer
{
    /// <summary>Stage 2: every pair of episodes, every offset. CPU-bound.</summary>
    IReadOnlyList<PairMatches> Match(IReadOnlyList<EpisodeSignals> episodes, CancellationToken ct);

    /// <summary>
    /// Stages 3 and 7: theme candidates (two passes) and credits. A season with
    /// fewer than <see cref="SegmentRules.MinEpisodesForThemes"/> episodes, or a
    /// movie, gets no theme candidates: themes need partners.
    /// </summary>
    SeasonCandidates FindCandidates(
        IReadOnlyList<EpisodeSignals> episodes,
        IReadOnlyList<PairMatches> matches
    );

    /// <summary>Stages 4–8 with the picture evidence the candidates asked for.</summary>
    IReadOnlyList<EpisodeSegments> Decide(SeasonCandidates candidates, SeasonEvidence evidence);
}
