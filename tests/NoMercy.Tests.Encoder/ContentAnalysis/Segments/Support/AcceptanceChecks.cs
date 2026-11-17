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

using Newtonsoft.Json.Linq;
using NoMercy.Encoder.ContentAnalysis.Segments;

namespace NoMercy.Tests.Encoder.ContentAnalysis.Segments.Support;

/// <summary>
/// The #598 acceptance rules applied to one season's answer. Returns every
/// broken rule; an empty list is a pass.
/// </summary>
public static class AcceptanceChecks
{
    /// <summary>No intro or credits start may come earlier than the truth by more than this.</summary>
    public const double EarlyLimitSeconds = 2.0;

    public static List<string> Failures(
        RecordedSeason season,
        IReadOnlyList<EpisodeSegments> result
    )
    {
        IssueTableRow row = IssueTableRow.For(season.Id);
        List<string> failures = [];

        int intros = result.Count(e => e.Intro is not null);
        int credits = result.Count(e => e.CreditsStart is not null);
        if (intros != row.Intros)
            failures.Add($"{season.Id}: {intros} intros, table {row.Intros}");
        if (credits != row.Credits)
            failures.Add($"{season.Id}: {credits} credits starts, table {row.Credits}");

        if (row.ChapterHits is not null)
        {
            ChapterScore score = ChapterScore.Zero;
            for (int i = 0; i < result.Count; i++)
                score += ChapterScore.Of(season.Id, season.Episodes[i], result[i]);
            if (score.Hit < row.ChapterHits)
                failures.Add(
                    $"{season.Id}: {score.Hit}/{score.Truth} chapter segments, table {row.ChapterHits}"
                );
            if (row.Within5 is not null && score.Within5 < row.Within5)
                failures.Add($"{season.Id}: {score.Within5} within 5 s, table {row.Within5}");
            if (row.Within2 is not null && score.Within2 < row.Within2)
                failures.Add($"{season.Id}: {score.Within2} within 2 s, table {row.Within2}");
        }

        if (row.FrameTruth)
            failures.AddRange(NeverEarlierThanFrameChecks(season, result));

        return failures;
    }

    /// <summary>
    /// Frame-checked seasons: the prototype's answer was checked frame by frame
    /// and holds no false intro and no early credits start. Nothing may be
    /// skipped that it let play: no intro or credits where it had none, and no
    /// intro or credits start more than 2 s earlier than its own.
    /// </summary>
    private static IEnumerable<string> NeverEarlierThanFrameChecks(
        RecordedSeason season,
        IReadOnlyList<EpisodeSegments> result
    )
    {
        JArray checkedEpisodes = (JArray)season.PrototypeAnalysis["episodes"]!;
        for (int i = 0; i < result.Count; i++)
        {
            EpisodeSegments mine = result[i];
            JToken final = checkedEpisodes[i]["final"]!;
            JToken? intro = ((JArray)final["segments"]!).FirstOrDefault(s =>
                (string)s["kind"]! == "intro"
            );
            double? creditsStart = (double?)final["credits_start"];
            string where = $"{season.Id}/{mine.Key}";

            if (mine.Intro is not null && intro is null)
                yield return $"{where}: intro {mine.Intro.Start}-{mine.Intro.End} where the frame check found none";
            if (
                mine.Intro is not null
                && intro is not null
                && mine.Intro.Start < (double)intro["start"]! - EarlyLimitSeconds
            )
                yield return $"{where}: intro starts {mine.Intro.Start}, frame check {(double)intro["start"]!}";
            if (mine.CreditsStart is not null && creditsStart is null)
                yield return $"{where}: credits at {mine.CreditsStart} where the frame check found none";
            if (
                mine.CreditsStart is not null
                && creditsStart is not null
                && mine.CreditsStart < creditsStart - EarlyLimitSeconds
            )
                yield return $"{where}: credits start {mine.CreditsStart}, frame check {creditsStart}";
        }
    }
}
