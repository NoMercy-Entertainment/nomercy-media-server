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

namespace NoMercy.Tests.MediaProcessing.Parsing;

/// <summary>
/// Every release name that the public parsers test (and the ones we added)
/// reaches its title, year, season and episode through the production
/// resolver: once as a bare file, once inside a folder named like the release.
/// </summary>
public class SceneCorpusTests
{
    public static TheoryData<string, CorpusMode> SourceAndMode()
    {
        TheoryData<string, CorpusMode> data = new();
        foreach (string source in SceneCorpus.Sources)
        {
            foreach (CorpusMode mode in Enum.GetValues<CorpusMode>())
                data.Add(source, mode);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SourceAndMode))]
    public void Every_name_of_a_source_reaches_what_the_source_asserts(
        string source,
        CorpusMode mode
    )
    {
        int total = SceneCorpus.Entries.Count(entry => entry.SourceRepo == source);
        List<CorpusMismatch> failing =
        [
            .. SceneCorpusRun.Mismatches.Where(m =>
                m.Entry.SourceRepo == source && m.Mode == mode && !SceneCorpus.IsKnown(m)
            ),
        ];
        int failingNames = failing.Select(m => m.Entry).Distinct().Count();

        failing
            .Should()
            .BeEmpty(
                $"{source} {mode}: {failingNames} of {total} names differ. "
                    + string.Join(" || ", failing.Take(12))
            );
    }

    [Fact]
    public void Every_known_difference_carries_a_written_reason()
    {
        IEnumerable<string> withoutReason = SceneCorpus
            .KnownDifferences.Where(known => string.IsNullOrWhiteSpace(known.Reason))
            .Select(known => $"{known.Name} ({known.Field})");

        withoutReason.Should().BeEmpty();
    }

    [Fact]
    public void Every_known_difference_still_differs()
    {
        HashSet<(string, string, string)> live =
        [
            .. SceneCorpusRun.Mismatches.Select(m => (m.Entry.Name, m.Field, m.Ours)),
        ];

        IEnumerable<string> stale = SceneCorpus
            .KnownDifferences.Where(known => !live.Contains((known.Name, known.Field, known.Ours)))
            .Select(known => $"{known.Name} ({known.Field}: ours '{known.Ours}')");

        stale.Should().BeEmpty("a known difference that no longer happens is a stale excuse");
    }

    [Fact]
    public void The_corpus_holds_the_nomercy_fansub_batches()
    {
        SceneCorpus
            .Entries.Where(entry => entry.SourceRepo == "nomercy")
            .Select(entry => entry.Title)
            .Should()
            .Contain("Battle Programmer Shirase")
            .And.Contain("Kamiina Botan, Yoeru Sugata wa Yuri no Hana");
        SceneCorpus.Entries.Count.Should().BeGreaterThanOrEqualTo(3371);
    }
}
