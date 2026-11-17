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

namespace NoMercy.MediaProcessing.Segments;

/// <summary>
/// The one list of chapter titles the server recognises. It is the merged list
/// of every client (web segmentSkip, KMP Android ChapterTitlePatterns, iOS
/// ChapterSkip); client fallback lists for older servers should equal it.
/// Matching ignores case and surrounding spaces.
/// </summary>
public static class ChapterTitlePatterns
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>An opening song.</summary>
    public static readonly IReadOnlyList<string> Opening =
    [
        "^OP$",
        "^NCOP$",
        "^Opening$",
        "^Opening Credits$",
        "^Opening Theme$",
        "^Opening Song$",
    ];

    /// <summary>
    /// "Intro". Many anime releases use it for a cold open (story), so it counts
    /// as an opening only at an opening song's length (see the classifier).
    /// </summary>
    public const string Intro = "^Intro$";

    /// <summary>A recap of earlier episodes.</summary>
    public static readonly IReadOnlyList<string> Recap = ["^Recap$", "^Previously"];

    /// <summary>The ending song or the credits.</summary>
    public static readonly IReadOnlyList<string> Ending =
    [
        "^ED$",
        "^NCED$",
        "^Ending$",
        "^ED\\+Cast$",
        "^Outro$",
        "^Credits$",
        "^End Credits$",
        "^Closing$",
    ];

    /// <summary>What follows the ending: a next-episode preview or a commercial.</summary>
    public static readonly IReadOnlyList<string> AfterEnding =
    [
        "^Preview$",
        "^Next Episode Preview$",
        "^Next Time Preview$",
        "^PV$",
        "^CM$",
    ];

    /// <summary>
    /// A title that says nothing about the content: empty, "Chapter 02",
    /// "Scene 3", "S01E04 Chapter 1", a bare number or time, or the "Start"
    /// chapter the scan adds when the first chapter starts after 0.
    /// </summary>
    public const string Generic =
        @"^(?:S\d+E\d+\s+)?(?:(?:Chapter|Chapitre|Kapitel|Cap[ií]tulo|Hoofdstuk|Scene)\s*\d+|\d+|Start|\d{1,2}:\d{2}(?::\d{2})?(?:[.,]\d+)?)?$";

    private static readonly Regex[] OpeningRegexes = Compile(Opening);
    private static readonly Regex IntroRegex = Compile([Intro])[0];
    private static readonly Regex[] RecapRegexes = Compile(Recap);
    private static readonly Regex[] EndingRegexes = Compile(Ending);
    private static readonly Regex[] AfterEndingRegexes = Compile(AfterEnding);
    private static readonly Regex GenericRegex = Compile([Generic])[0];

    public static bool IsOpening(string? title) => Matches(OpeningRegexes, title);

    public static bool IsIntro(string? title) => Matches([IntroRegex], title);

    public static bool IsRecap(string? title) => Matches(RecapRegexes, title);

    public static bool IsEnding(string? title) => Matches(EndingRegexes, title);

    public static bool IsAfterEnding(string? title) => Matches(AfterEndingRegexes, title);

    public static bool IsGeneric(string? title) => Matches([GenericRegex], title);

    private static bool Matches(Regex[] regexes, string? title)
    {
        string trimmed = (title ?? string.Empty).Trim();
        return regexes.Any(regex => regex.IsMatch(trimmed));
    }

    private static Regex[] Compile(IReadOnlyList<string> patterns) =>
    [
        .. patterns.Select(pattern => new Regex(
            pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            MatchTimeout
        )),
    ];
}
