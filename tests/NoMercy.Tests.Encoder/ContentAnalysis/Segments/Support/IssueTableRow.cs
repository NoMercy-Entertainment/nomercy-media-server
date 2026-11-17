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

namespace NoMercy.Tests.Encoder.ContentAnalysis.Segments.Support;

/// <summary>
/// One row of the results table in #598, the acceptance bar: every number must
/// hold or improve.
/// </summary>
/// <param name="FrameTruth">Truth is frame checks (the prototype's checked answer), not chapters.</param>
/// <param name="Intros">Episodes with an intro.</param>
/// <param name="Credits">Episodes with a credits start.</param>
/// <param name="ChapterHits">Chapter segments found (chapter truth only).</param>
/// <param name="Within5">Chapter segments within 5 s at both edges.</param>
/// <param name="Within2">Chapter segments within 2 s at both edges.</param>
public sealed record IssueTableRow(
    string Season,
    bool FrameTruth,
    int Intros,
    int Credits,
    int? ChapterHits = null,
    int? Within5 = null,
    int? Within2 = null
)
{
    public static readonly IssueTableRow[] All =
    [
        // Hellsing S01: 13/13 intro, 13/13 ED; 38/38 chapter segments within 5 s.
        new("hellsing-s01", false, 13, 13, ChapterHits: 38, Within5: 38),
        // Rick and Morty S01: 11/11, 11/11; 32/32 within 5 s, 29 within 2 s.
        new("rick-and-morty-s01", false, 11, 11, ChapterHits: 32, Within5: 32, Within2: 29),
        // Rick and Morty S02: 8/8 (2 episodes have none), 10/10; 27/27 within 5 s.
        new("rick-and-morty-s02", false, 8, 10, ChapterHits: 27, Within5: 27),
        // American Dad S02: 5/5 full intros, 8/8 (credit starts: see the dedicated test).
        new("american-dad-s02", false, 5, 8),
        // The Last of Us S01: 4/4, 4/4 (its chapters have no milliseconds).
        new("the-last-of-us-s01", false, 4, 4),
        new("silo-s01", true, 6, 6),
        // Krapopolis: 6/6 intros; overlay credits, button only.
        new("krapopolis-s01", true, 6, 2),
        // Yakuza: 5/6 (E05 missed, on the safe side), 6/6.
        new("yakuza-s01", true, 5, 6),
        // Squid Game: no intro (correct), 6/6.
        new("squid-game-s01", true, 0, 6),
        // Hellsing Ultimate: no intro (correct), 6/6 from OCR only.
        new("hellsing-ultimate", true, 0, 6),
        // Chernobyl: studio cards (one), 5/5 with the finale epilogue kept.
        new("chernobyl", true, 1, 5),
        // Tomb Raider: 3/6 (logo + title card), 6/6.
        new("tomb-raider-s01", true, 3, 6),
        // Mr. Robot: a 5 s title card, not reported by design; 6/6.
        new("mr-robot-s01", true, 0, 6),
    ];

    public static IssueTableRow For(string season) => All.Single(r => r.Season == season);
}
