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

using FluentAssertions;
using NoMercy.MediaProcessing.DerivedAudio;
using DerivedAudioRow = NoMercy.Database.Models.Music.DerivedAudio;

namespace NoMercy.Tests.MediaProcessing.DerivedAudio;

// This test namespace shares its last segment with NoMercy.Database.Models.Music.DerivedAudio;
// the row type is aliased to DerivedAudioRow so the bare name resolves to the type, not the
// enclosing namespace (CS0118).
public class DerivedAudioEvictionTests
{
    private static DerivedAudioRow Row(string key, long bytes, DateTime lastUsed) =>
        new()
        {
            Key = key,
            ContentType = "audio/opus",
            Bytes = bytes,
            CreatedAt = lastUsed,
            LastUsedAt = lastUsed,
        };

    [Fact]
    public void UnderTheCap_NothingIsChosen()
    {
        DateTime now = new(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
        List<DerivedAudioRow> rows = [Row("a", 40, now.AddDays(-3)), Row("b", 40, now.AddDays(-2))];

        DerivedAudioEviction
            .Choose(rows, capBytes: 100, grace: TimeSpan.FromHours(24), now)
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void OverTheCap_TheLongestUnusedGoFirst_UntilUnderTheCap()
    {
        DateTime now = new(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
        List<DerivedAudioRow> rows =
        [
            Row("old", 50, now.AddDays(-5)),
            Row("mid", 50, now.AddDays(-3)),
            Row("new", 50, now.AddDays(-2)),
        ];

        DerivedAudioEviction
            .Choose(rows, capBytes: 100, grace: TimeSpan.FromHours(24), now)
            .Select(row => row.Key)
            .Should()
            .Equal("old");
    }

    [Fact]
    public void RowsInsideTheGrace_AreNeverChosen_EvenOverTheCap()
    {
        DateTime now = new(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
        List<DerivedAudioRow> rows =
        [
            Row("fresh", 500, now.AddHours(-1)),
            Row("fresh2", 500, now.AddHours(-2)),
        ];

        DerivedAudioEviction
            .Choose(rows, capBytes: 100, grace: TimeSpan.FromHours(24), now)
            .Should()
            .BeEmpty();
    }
}
