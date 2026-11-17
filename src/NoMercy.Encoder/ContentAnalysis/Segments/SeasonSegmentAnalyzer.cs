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

/// <inheritdoc />
public sealed class SeasonSegmentAnalyzer : ISeasonSegmentAnalyzer
{
    /// <summary>Matching shares the box with playback and encodes: half the cores at most.</summary>
    private static readonly int MatchParallelism = Math.Max(1, Environment.ProcessorCount / 2);

    public IReadOnlyList<PairMatches> Match(
        IReadOnlyList<EpisodeSignals> episodes,
        CancellationToken ct
    ) => MatchPairs(episodes, ChromaprintRaw.SecondsPerHash, MatchParallelism, ct);

    public SeasonCandidates FindCandidates(
        IReadOnlyList<EpisodeSignals> episodes,
        IReadOnlyList<PairMatches> matches
    )
    {
        List<IReadOnlyList<CreditsBlock>> credits =
        [
            .. episodes.Select(e => (IReadOnlyList<CreditsBlock>)CreditsFinder.Find(e)),
        ];

        if (episodes.Count < SegmentRules.MinEpisodesForThemes)
        {
            List<IReadOnlyList<PartnerHit>> none = [.. episodes.Select(_ => Array.Empty<PartnerHit>())];
            List<IReadOnlyList<PendingCandidate>> nothing =
            [
                .. episodes.Select(_ => Array.Empty<PendingCandidate>()),
            ];
            return new(episodes, none, nothing, credits);
        }

        List<IReadOnlyList<PartnerHit>> hits = HitsByEpisode(episodes.Count, matches);
        List<double> durations = [.. episodes.Select(e => e.DurationSeconds)];

        // Pass 1: the same-zone rule. Pass 2: a partner also counts when the audio
        // falls inside one of ITS pass-1 themes or credits.
        List<IReadOnlyList<TimeRange>> pass1 = [];
        for (int i = 0; i < episodes.Count; i++)
        {
            List<TimeRange> themes =
            [
                .. ThemeFinder.Find(i, durations, hits[i]).Select(r => new TimeRange(r.Start, r.End)),
                .. credits[i].Select(c => new TimeRange(c.Start, c.End)),
            ];
            pass1.Add(themes);
        }

        List<IReadOnlyList<PendingCandidate>> pending = [];
        for (int i = 0; i < episodes.Count; i++)
        {
            EpisodeSignals episode = episodes[i];
            List<PendingCandidate> regions = [];
            foreach (ThemeRegion region in ThemeFinder.Find(i, durations, hits[i], pass1))
            {
                (double start, bool startOnSilence) = EdgeConfirmer.Snap(
                    episode.Silences,
                    region.Start,
                    isStart: true
                );
                (double end, bool endOnSilence) = EdgeConfirmer.Snap(
                    episode.Silences,
                    region.End,
                    isStart: false
                );
                regions.Add(
                    new(
                        region,
                        SegmentMath.Round(start, 2),
                        startOnSilence,
                        SegmentMath.Round(end, 2),
                        endOnSilence
                    )
                );
            }

            pending.Add(regions);
        }

        return new(episodes, hits, pending, credits);
    }

    public IReadOnlyList<EpisodeSegments> Decide(
        SeasonCandidates candidates,
        SeasonEvidence evidence
    )
    {
        IReadOnlyList<EpisodeSignals> episodes = candidates.Episodes;
        int partners = episodes.Count - 1;
        List<EpisodeSegments> season = [];

        for (int i = 0; i < episodes.Count; i++)
        {
            EpisodeSignals episode = episodes[i];
            List<SegmentCandidate> decided = [];
            foreach (PendingCandidate pending in candidates.Pending[i])
            {
                ThemeRegion region = pending.Region;
                (double start, EdgeEvidence startEdge) = EdgeConfirmer.Confirm(
                    episode.DurationSeconds,
                    episode.Silences,
                    evidence.EdgeProbes[i],
                    pending.ProbeStart,
                    isStart: true
                );
                (double end, EdgeEvidence endEdge) = EdgeConfirmer.Confirm(
                    episode.DurationSeconds,
                    episode.Silences,
                    evidence.EdgeProbes[i],
                    pending.ProbeEnd,
                    isStart: false
                );
                PictureSupport? picture = PictureMatcher.Support(
                    i,
                    region.Start,
                    region.End,
                    candidates.Hits[i],
                    evidence.Thumbnails
                );
                bool strong = CandidateRules.StrongAudio(region, end - start, partners);

                decided.Add(
                    new(
                        region.Kind,
                        new(region.Start, region.End),
                        new(pending.ProbeStart, pending.ProbeEnd),
                        SegmentMath.Round(start, 2),
                        SegmentMath.Round(end, 2),
                        SegmentMath.Round(end - start, 2),
                        region.Support,
                        region.Consistent,
                        partners,
                        region.Coverage,
                        pending.StartOnSilence,
                        EdgeConfirmer.NearBlack(episode.Black, pending.ProbeStart),
                        pending.EndOnSilence,
                        EdgeConfirmer.NearBlack(episode.Black, pending.ProbeEnd),
                        startEdge,
                        endEdge,
                        picture,
                        strong,
                        CandidateRules.Keep(
                            region.Kind,
                            end - start,
                            startEdge,
                            endEdge,
                            picture?.Confirmed,
                            strong
                        )
                    )
                );
            }

            season.Add(
                SegmentCombiner.Combine(
                    episode,
                    decided,
                    candidates.Credits[i],
                    CreditsFinder.FindContentEnd(episode)
                )
            );
        }

        return SegmentCombiner.ApplySeasonCreditsLength(season);
    }

    /// <summary>Every pair (in season order), matched in parallel.</summary>
    internal static List<PairMatches> MatchPairs(
        IReadOnlyList<EpisodeSignals> episodes,
        double secondsPerHash,
        int parallelism,
        CancellationToken ct
    )
    {
        List<(int X, int Y)> pairs = [];
        for (int x = 0; x < episodes.Count; x++)
        {
            for (int y = x + 1; y < episodes.Count; y++)
                pairs.Add((x, y));
        }

        PairMatches[] results = new PairMatches[pairs.Count];
        ParallelOptions options = new()
        {
            MaxDegreeOfParallelism = parallelism,
            CancellationToken = ct,
        };
        Parallel.For(
            0,
            pairs.Count,
            options,
            k =>
            {
                (int x, int y) = pairs[k];
                results[k] = new(
                    x,
                    y,
                    FingerprintMatcher.MatchEpisodes(
                        episodes[x].Fingerprint,
                        episodes[y].Fingerprint,
                        secondsPerHash
                    )
                );
            }
        );

        return [.. results];
    }

    /// <summary>Each episode's hits: x gets (x, partner y), y gets the mirror, in pair order.</summary>
    private static List<IReadOnlyList<PartnerHit>> HitsByEpisode(
        int episodes,
        IReadOnlyList<PairMatches> matches
    )
    {
        List<PartnerHit>[] hits = new List<PartnerHit>[episodes];
        for (int i = 0; i < episodes; i++)
            hits[i] = [];

        foreach (PairMatches pair in matches.OrderBy(p => p.X).ThenBy(p => p.Y))
        {
            foreach (AudioMatch match in pair.Matches)
            {
                hits[pair.X].Add(new(match.XStart, match.XEnd, pair.Y, match.YStart, match.YEnd));
                hits[pair.Y].Add(new(match.YStart, match.YEnd, pair.X, match.XStart, match.XEnd));
            }
        }

        return [.. hits];
    }
}
