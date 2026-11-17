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
/// Stage 7: credits from the picture, and the end of content.
/// <para>
/// Credits are long runs of dark frames (blackdetect pix_th 0.10, pic_th 0.80,
/// merged over gaps of up to 6 s, at least 20 s) or of text-heavy frames (OCR,
/// at least 15 s; Hellsing Ultimate's credits roll over a textured background).
/// Dark story scenes pass the dark-frame test too (Hellsing is a dark anime), so
/// a block only counts when it ends near the end of the episode.
/// </para>
/// </summary>
public static class CreditsFinder
{
    public static List<CreditsBlock> Find(EpisodeSignals episode)
    {
        double duration = episode.DurationSeconds;
        double limit = Math.Max(
            SegmentRules.CreditsTailSeconds,
            SegmentRules.CreditsTailFraction * duration
        );
        List<CreditsBlock> blocks = [];
        OcrScan? ocr = episode.Ocr;
        List<double> textFrames = ocr is null
            ? []
            : [.. ocr.Samples.Where(s => s.Words >= SegmentRules.OcrWords).Select(s => s.Time)];

        if (episode.Black is not null)
        {
            List<TimeRange> dark = SegmentMath.Merge(
                episode.Black.Loose.Where(r => r.Start > duration / 2),
                SegmentRules.CreditsGapSeconds
            );
            foreach (TimeRange run in dark)
            {
                if (run.Length < SegmentRules.CreditsMinSeconds || duration - run.End > limit)
                    continue;

                double start = run.Start;
                if (ocr is not null && textFrames.Count > 0 && ocr.StartSeconds <= start)
                    start = StartAtFirstText(start, run.End, textFrames, episode.Black.Strict);

                // Re-checked after the refinement: a dark last scene can shrink a
                // block below the minimum (a 12 s scrap in Hellsing Ultimate E05).
                if (run.End - start >= SegmentRules.CreditsMinSeconds)
                    blocks.Add(new(start, run.End, FromDarkFrames: true, FromText: false));
            }
        }

        if (ocr is not null)
        {
            List<TimeRange> text = SegmentMath.Merge(
                textFrames.Select(t => new TimeRange(t, t + 1.0)),
                SegmentRules.OcrGapSeconds
            );
            foreach (TimeRange run in text)
            {
                if (run.Length >= SegmentRules.OcrMinSeconds && duration - run.End <= limit)
                    blocks.Add(new(run.Start, run.End, FromDarkFrames: false, FromText: true));
            }
        }

        return MergeBlocks(blocks, SegmentRules.CreditsMergeSeconds);
    }

    /// <summary>
    /// Trailing dead air: where the silence and the plain black that reach the end
    /// start. With both, the later start wins.
    /// </summary>
    public static ContentEnd FindContentEnd(EpisodeSignals episode)
    {
        double duration = episode.DurationSeconds;
        List<double> silence =
        [
            .. episode
                .Silences.Where(s => s.End >= duration - SegmentRules.TailSilenceSeconds)
                .Select(s => s.Start),
        ];
        List<double> black =
        [
            .. (episode.Black?.Strict ?? [])
                .Where(b => b.End >= duration - SegmentRules.TailBlackSeconds)
                .Select(b => b.Start),
        ];

        if (silence.Count > 0 && black.Count > 0)
            return new(Math.Max(silence.Min(), black.Min()), ContentEndEvidence.BlackAndSilence);
        if (black.Count > 0)
            return new(black.Min(), ContentEndEvidence.Black);
        if (silence.Count > 0)
            return new(silence.Min(), ContentEndEvidence.Silence);
        return new(duration, ContentEndEvidence.FileEnd);
    }

    /// <summary>
    /// A dark last scene runs into dark credits (Yakuza E04: story until 48:38,
    /// dark from 48:27). With OCR over the block the start moves to the first text
    /// frame (no distance cap: Chernobyl E02 has 2 min of dark story first), then
    /// back over the one plain-black run touching it (Yakuza E01, Tomb Raider: 3–10 s
    /// of black, then text). One run only: chaining walked through a night scene
    /// full of short black flashes (Squid Game E02). Late is safe, early skips story.
    /// </summary>
    private static double StartAtFirstText(
        double start,
        double end,
        List<double> textFrames,
        IReadOnlyList<TimeRange> strictBlack
    )
    {
        double? first = null;
        foreach (double t in textFrames)
        {
            if (t >= start - 2 && t <= end)
            {
                first = t;
                break;
            }
        }

        if (first is null)
            return start;

        double moved = Math.Max(start, first.Value - 1.0);
        List<double> touching =
        [
            .. strictBlack.Where(b => b.Start < moved && moved <= b.End + 1.0).Select(b => b.Start),
        ];
        return touching.Count > 0 ? touching.Max() : moved;
    }

    private static List<CreditsBlock> MergeBlocks(List<CreditsBlock> blocks, double gap)
    {
        List<CreditsBlock> merged = [];
        foreach (CreditsBlock block in blocks.OrderBy(b => b.Start).ThenBy(b => b.End))
        {
            if (merged.Count > 0 && block.Start <= merged[^1].End + gap)
            {
                CreditsBlock last = merged[^1];
                merged[^1] = last with
                {
                    End = Math.Max(last.End, block.End),
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
}
