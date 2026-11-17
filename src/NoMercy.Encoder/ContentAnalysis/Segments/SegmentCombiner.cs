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
/// Stage 8: one answer per episode, then one season-wide correction.
/// </summary>
public static class SegmentCombiner
{
    /// <summary>
    /// The intro (most partners at the same place, then the longest), the outro
    /// block, any content after it, <c>credits_start</c> and <c>content_end</c>.
    /// </summary>
    public static EpisodeSegments Combine(
        EpisodeSignals episode,
        IReadOnlyList<SegmentCandidate> candidates,
        IReadOnlyList<CreditsBlock> credits,
        ContentEnd contentEnd
    )
    {
        // OrderByDescending is stable: equal keys keep their time order.
        SegmentCandidate? intro = candidates
            .Where(c => c.Keep && c.Kind == ThemeKind.Intro)
            .OrderByDescending(c => c.Consistent)
            .ThenByDescending(c => c.Length)
            .FirstOrDefault();

        List<SegmentCandidate> endings =
        [
            .. candidates
                .Where(c => c.Keep && c.Kind == ThemeKind.Outro)
                .OrderByDescending(c => c.Consistent)
                .ThenByDescending(c => c.Start),
        ];
        SegmentCandidate? confirmed = endings.FirstOrDefault(c => c.PictureConfirmed == true);

        List<CreditsBlock> usedCredits;
        SegmentCandidate? audioOutro;
        if (confirmed is not null)
        {
            // Same sound AND same picture elsewhere: a real ending. Dark story
            // scenes pass the credits test (Hellsing E07), so credits must touch it.
            audioOutro = confirmed;
            usedCredits = [.. credits.Where(c => Touches(confirmed, c.Start, c.End))];
        }
        else if (credits.Count > 0)
        {
            // The picture shows credits: they win, and only the last block (the
            // longest picked a long dark scene over Silo's 64 s credits). An audio
            // theme only extends them. Strong audio alone was wrong in Squid Game.
            usedCredits = [credits[^1]];
            audioOutro = endings.FirstOrDefault(c =>
                Touches(c, usedCredits[0].Start, usedCredits[0].End)
            );
        }
        else
        {
            usedCredits = [];
            audioOutro = endings.FirstOrDefault(c => c.StrongAudio || c.PictureConfirmed is null);
        }

        List<Block> blocks =
        [
            .. usedCredits.Select(c => new Block(
                c.Start,
                c.End,
                OutroSources.Credits,
                c.FromDarkFrames,
                c.FromText
            )),
        ];

        if (audioOutro is not null)
            AddAudio(blocks, audioOutro);

        List<OutroBlock> outros =
        [
            .. MergeBlocks(blocks)
                .Select(b => new OutroBlock(
                    SegmentMath.Round(b.Start, 2),
                    SegmentMath.Round(b.End, 2),
                    SegmentMath.Round(b.End - b.Start, 2),
                    b.Sources,
                    b.FromDarkFrames,
                    b.FromText
                )),
        ];

        ContentEnd end = contentEnd with { Seconds = SegmentMath.Round(contentEnd.Seconds, 2) };
        double? creditsStart = null;
        TimeRange? afterOutro = null;
        if (outros.Count > 0)
        {
            OutroBlock last = outros[^1];
            creditsStart = last.Start;
            double gap = contentEnd.Seconds - last.End;
            if (gap > 0 && gap < SegmentRules.LogoTailSeconds)
            {
                // Logos and end cards: part of the credits.
                outros[^1] = last with
                {
                    End = end.Seconds,
                    Length = SegmentMath.Round(contentEnd.Seconds - last.Start, 2),
                    Sources = last.Sources | OutroSources.Logos,
                };
            }
            else if (gap >= SegmentRules.LogoTailSeconds)
            {
                // Epilogues, previews, teasers: content, never skipped.
                afterOutro = new TimeRange(last.End, end.Seconds);
            }
        }

        return new(
            episode.Key,
            episode.DurationSeconds,
            intro,
            outros,
            afterOutro,
            creditsStart,
            end,
            candidates,
            credits
        );
    }

    /// <summary>
    /// Credits run about as long in every episode of a season (Chernobyl 161–169 s).
    /// A block far longer than the season median swallowed story before it
    /// (Chernobyl E05: a 4.5 min finale epilogue of photos and text; E02: 2 min of
    /// a dark scene), so its start moves to end − median. Only ever later.
    /// </summary>
    public static List<EpisodeSegments> ApplySeasonCreditsLength(
        IReadOnlyList<EpisodeSegments> season
    )
    {
        List<double> lengths =
        [
            .. season
                .SelectMany(e => e.Outros)
                .Where(o => o.Sources.HasFlag(OutroSources.Credits))
                .Select(o => o.Length),
        ];
        if (lengths.Count < SegmentRules.CreditsMedianMinBlocks)
            return [.. season];

        lengths.Sort();
        double median = lengths[lengths.Count / 2];

        List<EpisodeSegments> result = [];
        foreach (EpisodeSegments episode in season)
        {
            double? creditsStart = episode.CreditsStart;
            List<OutroBlock> outros = [];
            foreach (OutroBlock outro in episode.Outros)
            {
                double moved = SegmentMath.Round(outro.End - median, 2);
                if (
                    outro.Sources.HasFlag(OutroSources.Credits)
                    && outro.Length > SegmentRules.CreditsOutlierFactor * median
                    && moved > outro.Start
                )
                {
                    outros.Add(
                        outro with
                        {
                            Start = moved,
                            Length = SegmentMath.Round(outro.End - moved, 2),
                            Sources = outro.Sources | OutroSources.SeasonLength,
                        }
                    );
                    if (creditsStart is not null)
                        creditsStart = Math.Max(creditsStart.Value, moved);
                }
                else
                {
                    outros.Add(outro);
                }
            }

            result.Add(episode with { Outros = outros, CreditsStart = creditsStart });
        }

        return result;
    }

    private static bool Touches(SegmentCandidate ending, double start, double end) =>
        ending.Start <= end + SegmentRules.TouchSeconds
        && ending.End >= start - SegmentRules.TouchSeconds;

    /// <summary>
    /// The ending theme joins the credits block it touches (the song can start
    /// before the first credit text: anime endings ~19 s), or stands alone.
    /// </summary>
    private static void AddAudio(List<Block> blocks, SegmentCandidate ending)
    {
        for (int i = 0; i < blocks.Count; i++)
        {
            Block block = blocks[i];
            if (!Touches(ending, block.Start, block.End))
                continue;

            blocks[i] = block with
            {
                Start = Math.Min(block.Start, ending.Start),
                End = Math.Max(block.End, ending.End),
                Sources = block.Sources | OutroSources.Audio,
            };
            return;
        }

        blocks.Add(new(ending.Start, ending.End, OutroSources.Audio, false, false));
    }

    private static List<Block> MergeBlocks(List<Block> blocks)
    {
        List<Block> merged = [];
        foreach (Block block in blocks.OrderBy(b => b.Start))
        {
            if (merged.Count > 0 && block.Start <= merged[^1].End + SegmentRules.TouchSeconds)
            {
                Block last = merged[^1];
                merged[^1] = last with
                {
                    End = Math.Max(last.End, block.End),
                    Sources = last.Sources | block.Sources,
                    FromDarkFrames = last.FromDarkFrames || block.FromDarkFrames,
                    FromText = last.FromText || block.FromText,
                };
            }
            else
            {
                merged.Add(block);
            }
        }

        return merged;
    }

    private readonly record struct Block(
        double Start,
        double End,
        OutroSources Sources,
        bool FromDarkFrames,
        bool FromText
    );
}
