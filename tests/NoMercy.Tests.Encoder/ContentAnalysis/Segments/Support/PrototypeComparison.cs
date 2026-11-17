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

using System.Globalization;
using Newtonsoft.Json.Linq;
using NoMercy.Encoder.ContentAnalysis.Segments;

namespace NoMercy.Tests.Encoder.ContentAnalysis.Segments.Support;

/// <summary>
/// Compares the C# answer with the prototype's analysis.json, field by field,
/// and lists every difference (an empty list means the port is faithful).
/// </summary>
public static class PrototypeComparison
{
    public static List<string> Differences(
        RecordedSeason season,
        IReadOnlyList<EpisodeSegments> result,
        double tolerance,
        bool compareCandidates
    )
    {
        List<string> differences = [];
        JArray expected = (JArray)season.PrototypeAnalysis["episodes"]!;
        if (expected.Count != result.Count)
        {
            differences.Add($"{season.Id}: {result.Count} episodes, prototype {expected.Count}");
            return differences;
        }

        for (int i = 0; i < result.Count; i++)
        {
            EpisodeSegments mine = result[i];
            JToken theirs = expected[i];
            string where = $"{season.Id}/{mine.Key}";
            JToken final = theirs["final"]!;

            List<(string Kind, double Start, double End, string Src)> want =
            [
                .. ((JArray)final["segments"]!).Select(s =>
                    (
                        (string)s["kind"]!,
                        (double)s["start"]!,
                        (double)s["end"]!,
                        Normalise((string?)s["src"])
                    )
                ),
            ];
            List<(string Kind, double Start, double End, string Src)> got = FinalSegments(mine);
            if (want.Count != got.Count)
            {
                differences.Add($"{where}: final {Show(got)} vs prototype {Show(want)}");
            }
            else
            {
                for (int k = 0; k < want.Count; k++)
                {
                    if (
                        want[k].Kind != got[k].Kind
                        || !Near(want[k].Start, got[k].Start, tolerance)
                        || !Near(want[k].End, got[k].End, tolerance)
                        || want[k].Src != got[k].Src
                    )
                        differences.Add(
                            $"{where}: final {Show([got[k]])} vs prototype {Show([want[k]])}"
                        );
                }
            }

            double? creditsStart = (double?)final["credits_start"];
            if (!Near(creditsStart, mine.CreditsStart, tolerance))
                differences.Add($"{where}: credits_start {mine.CreditsStart} vs {creditsStart}");
            double contentEnd = (double)final["content_end"]!;
            if (!Near(contentEnd, mine.ContentEnd.Seconds, tolerance))
                differences.Add($"{where}: content_end {mine.ContentEnd.Seconds} vs {contentEnd}");

            if (compareCandidates)
                CompareCandidates(where, theirs, mine, tolerance, differences);
        }

        return differences;
    }

    /// <summary>
    /// Matches computed in C# against the prototype's matches.json. The recorded
    /// times are rounded to 1 ms, so 2 ms is the tolerance.
    /// </summary>
    public static List<string> MatchDifferences(
        RecordedSeason season,
        IReadOnlyList<PairMatches> pairs
    )
    {
        List<string> differences = [];
        foreach (PairMatches pair in pairs)
        {
            PairMatches? recorded = season.PrototypeMatches.SingleOrDefault(p =>
                p.X == pair.X && p.Y == pair.Y
            );
            List<AudioMatch> want = recorded is null
                ? []
                : [.. recorded.Matches.OrderBy(m => m.XStart)];
            List<AudioMatch> got = [.. pair.Matches.OrderBy(m => m.XStart)];
            if (want.Count != got.Count)
            {
                differences.Add($"{season.Id} {pair.X}|{pair.Y}: {got.Count} runs vs {want.Count}");
                continue;
            }

            for (int k = 0; k < want.Count; k++)
            {
                if (
                    Math.Abs(want[k].XStart - got[k].XStart) > 0.002
                    || Math.Abs(want[k].XEnd - got[k].XEnd) > 0.002
                    || Math.Abs(want[k].YStart - got[k].YStart) > 0.002
                    || Math.Abs(want[k].YEnd - got[k].YEnd) > 0.002
                )
                    differences.Add($"{season.Id} {pair.X}|{pair.Y}: {got[k]} vs {want[k]}");
            }
        }

        return differences;
    }

    public static List<(string Kind, double Start, double End, string Src)> FinalSegments(
        EpisodeSegments episode
    )
    {
        List<(string Kind, double Start, double End, string Src)> segments = [];
        if (episode.Intro is not null)
            segments.Add(("intro", episode.Intro.Start, episode.Intro.End, ""));
        foreach (OutroBlock outro in episode.Outros)
            segments.Add(("outro", outro.Start, outro.End, Sources(outro.Sources)));
        if (episode.AfterOutro is { } after)
            segments.Add(("after-outro", after.Start, after.End, "structure"));
        return [.. segments.OrderBy(s => s.Start)];
    }

    private static void CompareCandidates(
        string where,
        JToken theirs,
        EpisodeSegments mine,
        double tolerance,
        List<string> differences
    )
    {
        List<JToken> want =
        [
            .. ((JArray)theirs["segments"]!).Where(s => (string)s["kind"]! is "intro" or "outro"),
        ];
        if (want.Count != mine.Candidates.Count)
        {
            differences.Add(
                $"{where}: {mine.Candidates.Count} candidates vs prototype {want.Count}"
            );
            return;
        }

        for (int k = 0; k < want.Count; k++)
        {
            JToken w = want[k];
            SegmentCandidate g = mine.Candidates[k];
            List<string> fields = [];
            if ((string)w["kind"]! != (g.Kind == ThemeKind.Intro ? "intro" : "outro"))
                fields.Add("kind");
            if (
                !Near((double)w["raw"]![0]!, g.Raw.Start, tolerance)
                || !Near((double)w["raw"]![1]!, g.Raw.End, tolerance)
            )
                fields.Add($"raw {g.Raw} vs {w["raw"]!.ToString(Newtonsoft.Json.Formatting.None)}");
            if (
                !Near((double)w["start"]!, g.Start, tolerance)
                || !Near((double)w["end"]!, g.End, tolerance)
            )
                fields.Add($"edges {g.Start}-{g.End} vs {w["start"]}-{w["end"]}");
            if (
                (string)w["start_edge"]! != Edge(g.StartEdge)
                || (string)w["end_edge"]! != Edge(g.EndEdge)
            )
                fields.Add(
                    $"evidence {g.StartEdge}/{g.EndEdge} vs {w["start_edge"]}/{w["end_edge"]}"
                );
            if ((int)w["consistent"]! != g.Consistent || (int)w["support"]! != g.Support)
                fields.Add(
                    $"support {g.Consistent}/{g.Support} vs {w["consistent"]}/{w["support"]}"
                );
            if (!Near((double)w["coverage"]!, g.Coverage, 0.0005))
                fields.Add($"coverage {g.Coverage} vs {w["coverage"]}");
            string wantPicture =
                w["visual"]!.Type == JTokenType.Null
                    ? "none"
                    : $"{w["visual"]![0]}/{w["visual"]![1]}";
            string gotPicture = g.Picture is { } p ? $"{p.Matched}/{p.Checked}" : "none";
            if (wantPicture != gotPicture)
                fields.Add($"picture {gotPicture} vs {wantPicture}");
            if ((bool)w["strong_audio"]! != g.StrongAudio)
                fields.Add($"strong {g.StrongAudio}");
            if ((bool)w["keep"]! != g.Keep)
                fields.Add($"keep {g.Keep}");
            if (fields.Count > 0)
                differences.Add(
                    $"{where}: candidate {k} ({g.Raw.Start}-{g.Raw.End}) "
                        + string.Join("; ", fields)
                );
        }
    }

    private static string Edge(EdgeEvidence evidence) =>
        evidence == EdgeEvidence.Unprobed ? "" : evidence.ToString().ToLowerInvariant();

    private static string Sources(OutroSources sources)
    {
        List<string> tokens = [];
        if (sources.HasFlag(OutroSources.Credits))
            tokens.Add("credits");
        if (sources.HasFlag(OutroSources.Audio))
            tokens.Add("audio");
        if (sources.HasFlag(OutroSources.Logos))
            tokens.Add("logos");
        if (sources.HasFlag(OutroSources.SeasonLength))
            tokens.Add("season-length");
        return string.Join('+', tokens.Order(StringComparer.Ordinal));
    }

    private static string Normalise(string? src) =>
        src is null or "" ? ""
        : src == "structure" ? "structure"
        : string.Join('+', src.Split('+').Distinct().Order(StringComparer.Ordinal));

    private static bool Near(double? a, double? b, double tolerance) =>
        a is null ? b is null : b is not null && Math.Abs(a.Value - b.Value) <= tolerance;

    private static string Show(
        IEnumerable<(string Kind, double Start, double End, string Src)> segments
    ) =>
        string.Join(
            ", ",
            segments.Select(s =>
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{s.Kind} {s.Start:0.00}-{s.End:0.00} [{s.Src}]"
                )
            )
        );
}
