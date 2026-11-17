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
/// The analysis answer for one episode (stage 8).
/// </summary>
/// <param name="Intro">The kept opening, if any (at most one per episode).</param>
/// <param name="Outros">End blocks in time order; the last one holds the credits.</param>
/// <param name="AfterOutro">
/// 8 s or more of content after the credits: epilogues, previews, teasers after the
/// copyright card. It is content and must never be skipped.
/// </param>
/// <param name="CreditsStart">Where the "next episode" button may appear.</param>
/// <param name="ContentEnd">Where black and silence run to the end: auto-advance happens here.</param>
/// <param name="Candidates">Every stage-6 candidate, kept or not (evidence for the logs and tests).</param>
/// <param name="Credits">Stage-7 credits blocks before stage 8 chose among them.</param>
public sealed record EpisodeSegments(
    string Key,
    double DurationSeconds,
    SegmentCandidate? Intro,
    IReadOnlyList<OutroBlock> Outros,
    TimeRange? AfterOutro,
    double? CreditsStart,
    ContentEnd ContentEnd,
    IReadOnlyList<SegmentCandidate> Candidates,
    IReadOnlyList<CreditsBlock> Credits
)
{
    /// <summary>The block that holds the credits: the last one.</summary>
    public OutroBlock? Outro => Outros.Count > 0 ? Outros[^1] : null;
}
