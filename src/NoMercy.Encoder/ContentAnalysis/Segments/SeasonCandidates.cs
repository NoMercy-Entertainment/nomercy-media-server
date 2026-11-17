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
/// Stages 2–3 (and the credits of stage 7) for a whole season, plus the picture
/// evidence stages 4–5 will need: which edges to probe and which spans to take
/// thumbnails of. Indexes are season indexes into <see cref="Episodes"/>.
/// </summary>
public sealed class SeasonCandidates
{
    internal SeasonCandidates(
        IReadOnlyList<EpisodeSignals> episodes,
        IReadOnlyList<IReadOnlyList<PartnerHit>> hits,
        IReadOnlyList<IReadOnlyList<PendingCandidate>> pending,
        IReadOnlyList<IReadOnlyList<CreditsBlock>> credits,
        IReadOnlyList<int> partners
    )
    {
        Episodes = episodes;
        Partners = partners;
        Hits = hits;
        Pending = pending;
        Credits = credits;
    }

    public IReadOnlyList<EpisodeSignals> Episodes { get; }

    /// <summary>How many partners each episode was matched against (see <see cref="SeasonPartners"/>).</summary>
    public IReadOnlyList<int> Partners { get; }

    /// <summary>Each episode's shared audio with every partner.</summary>
    public IReadOnlyList<IReadOnlyList<PartnerHit>> Hits { get; }

    /// <summary>Each episode's stage-7 credits blocks.</summary>
    public IReadOnlyList<IReadOnlyList<CreditsBlock>> Credits { get; }

    internal IReadOnlyList<IReadOnlyList<PendingCandidate>> Pending { get; }

    /// <summary>Each episode's pass-2 theme regions.</summary>
    public IReadOnlyList<ThemeRegion> Regions(int episode) =>
        [.. Pending[episode].Select(p => p.Region)];

    /// <summary>
    /// The times to probe the picture at (±<see cref="SegmentRules.EdgeProbeSeconds"/>),
    /// one per candidate edge, rounded to 0.1 s and distinct.
    /// </summary>
    public IReadOnlyList<double> EdgeProbeTimes(int episode) =>
        [
            .. Pending[episode]
                .SelectMany(p =>
                    new[] { SegmentMath.Round(p.ProbeStart, 1), SegmentMath.Round(p.ProbeEnd, 1) }
                )
                .Distinct()
                .Order(),
        ];

    /// <summary>The spans to take thumbnails of: candidates of at least 12 s, padded by 2 s.</summary>
    public IReadOnlyList<TimeRange> ThumbnailSpans(int episode) =>
        [
            .. SegmentMath.Merge(
                Pending[episode]
                    .Select(p => p.Region)
                    .Where(r => r.End - r.Start >= SegmentRules.ThumbnailMinRegionSeconds)
                    .Select(r => new TimeRange(
                        Math.Max(0.0, r.Start - SegmentRules.ThumbnailPadSeconds),
                        r.End + SegmentRules.ThumbnailPadSeconds
                    )),
                0.0
            ),
        ];
}
