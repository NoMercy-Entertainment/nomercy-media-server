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

using System.Text.Json;

namespace NoMercy.Tests.MediaProcessing.Parsing;

/// <summary>
/// Titles are a one-way ratchet: a name may keep a title that differs from its
/// source only while it sits in the baseline, and the baseline only shrinks.
/// </summary>
public class SceneCorpusTitleTests
{
    private const int MaxTitleBaseline = 689;
    private const string BaselineLabel = "TITLE-BASELINE: not individually reviewed";

    private static readonly IReadOnlyList<TitleBaselineEntry> Baseline = LoadBaseline();

    private static IEnumerable<CorpusMismatch> TitleMismatches =>
        SceneCorpusRun.Mismatches.Where(m => m.Field == "title" && !SceneCorpus.IsKnown(m));

    private static (string, string, string) KeyOf(CorpusMismatch m) =>
        (m.Entry.Name, m.Entry.SourceRepo, m.Mode.ToString());

    [Fact]
    public void A_title_that_differs_is_in_the_baseline()
    {
        HashSet<(string, string, string)> baseline =
        [
            .. Baseline.Select(b => (b.Name, b.Source, b.Mode)),
        ];

        IEnumerable<string> regressions = TitleMismatches
            .Where(m => !baseline.Contains(KeyOf(m)))
            .Select(m => m.ToString())
            .Distinct();

        regressions.Should().BeEmpty("a new title difference is a regression");
    }

    [Fact]
    public void A_baseline_entry_that_no_longer_differs_is_deleted()
    {
        HashSet<(string, string, string)> live = [.. TitleMismatches.Select(KeyOf)];

        IEnumerable<string> stale = Baseline
            .Where(b => !live.Contains((b.Name, b.Source, b.Mode)))
            .Select(b => $"[{b.Mode}] {b.Name}");

        stale.Should().BeEmpty("the baseline only shrinks: delete the entry");
    }

    [Fact]
    public void The_baseline_does_not_grow()
    {
        Baseline.Count.Should().BeLessThanOrEqualTo(MaxTitleBaseline);
    }

    [Fact]
    public void The_baseline_holds_no_empty_title_and_only_the_honest_label()
    {
        Baseline.Where(b => b.Ours.Length == 0).Should().BeEmpty();
        Baseline.Where(b => b.Label != BaselineLabel).Should().BeEmpty();
    }

    private static List<TitleBaselineEntry> LoadBaseline()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "Parsing",
            "Corpus",
            "scene-corpus.title-baseline.json"
        );
        return JsonSerializer.Deserialize<List<TitleBaselineEntry>>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            ) ?? throw new InvalidDataException("title baseline holds no list.");
    }
}
