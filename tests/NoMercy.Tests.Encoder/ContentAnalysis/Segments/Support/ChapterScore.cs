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

using System.Text.RegularExpressions;
using NoMercy.Encoder.ContentAnalysis.Segments;

namespace NoMercy.Tests.Encoder.ContentAnalysis.Segments.Support;

/// <summary>
/// Scores an answer against chapter truth exactly as the research prototype did
/// (<c>analyze.py score()</c>), so the issue's "38/38 within 5 s" numbers can be
/// recomputed. Test-only: the server's own chapter classifier is #503's.
/// </summary>
public sealed partial record ChapterScore(int Truth, int Hit, int Within2, int Within5, int Extra)
{
    public static ChapterScore operator +(ChapterScore a, ChapterScore b) =>
        new(
            a.Truth + b.Truth,
            a.Hit + b.Hit,
            a.Within2 + b.Within2,
            a.Within5 + b.Within5,
            a.Extra + b.Extra
        );

    public static ChapterScore Zero => new(0, 0, 0, 0, 0);

    /// <summary>The chapter segments of the truth: (kind, start, end).</summary>
    public static List<(string Kind, double Start, double End)> TruthOf(
        string seasonId,
        IReadOnlyList<RecordedChapter> chapters
    )
    {
        List<(string Kind, double Start, double End)> truth = [];
        foreach (RecordedChapter chapter in chapters)
        {
            string title = chapter.Title.Trim();
            string? kind =
                IntroTitle().IsMatch(title) ? "intro"
                : OutroTitle().IsMatch(title) ? "outro"
                : PreviewTitle().IsMatch(title) ? "preview"
                : RecapTitle().IsMatch(title) ? "recap"
                : PostTitle().IsMatch(title) ? "post"
                : null;

            // Hellsing names the cold-open story part "Intro"; its opening is "OP".
            if (
                seasonId.StartsWith("hellsing", StringComparison.Ordinal)
                && title.Equals("intro", StringComparison.OrdinalIgnoreCase)
            )
                kind = null;
            if (kind is not null)
                truth.Add((kind, chapter.Start, chapter.End));
        }

        return truth;
    }

    public static ChapterScore Of(string seasonId, RecordedEpisode episode, EpisodeSegments answer)
    {
        List<(string Kind, double Start, double End)> truth = TruthOf(seasonId, episode.Chapters);
        List<(string Kind, double Start, double End, string Src)> found =
            PrototypeComparison.FinalSegments(answer);
        List<double> outroStarts = [.. truth.Where(t => t.Kind == "outro").Select(t => t.Start)];
        HashSet<int> used = [];
        int counted = 0;
        int hit = 0;
        int within2 = 0;
        int within5 = 0;

        foreach ((string kind, double start, double end) in truth)
        {
            // Story before the ending (a finale epilogue) is content, not a skip segment.
            if (kind == "post" && outroStarts.Count > 0 && start < outroStarts.Min())
                continue;

            string[] want = kind switch
            {
                "preview" => ["preview", "after-outro"],
                "post" => ["after-outro"],
                _ => [kind],
            };

            int best = -1;
            double bestOverlap = 0;
            for (int i = 0; i < found.Count; i++)
            {
                if (!want.Contains(found[i].Kind))
                    continue;
                double overlap = Math.Max(
                    0.0,
                    Math.Min(end, found[i].End) - Math.Max(start, found[i].Start)
                );
                if (best < 0 || overlap > bestOverlap)
                {
                    best = i;
                    bestOverlap = overlap;
                }
            }

            counted++;
            if (best < 0 || bestOverlap <= 0)
                continue;

            used.Add(best);
            hit++;
            double ds = Math.Abs(found[best].Start - start);
            double de = Math.Abs(found[best].End - end);
            if (ds <= 2 && de <= 2)
                within2++;
            if (ds <= 5 && de <= 5)
                within5++;
        }

        int extra = truth.Count > 0 ? found.Count - used.Count : 0;
        return new(counted, hit, within2, within5, extra);
    }

    [GeneratedRegex(
        "^(op|opening|intro|opening credits|title sequence)$",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex IntroTitle();

    [GeneratedRegex(
        "^(ed|ending|credits|end credits|outro)$",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex OutroTitle();

    [GeneratedRegex(
        "^(preview|next episode)$",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex PreviewTitle();

    [GeneratedRegex(
        "^(recap|previously)$",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex RecapTitle();

    [GeneratedRegex(
        "^(epilogue|post.credits)$",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex PostTitle();
}
