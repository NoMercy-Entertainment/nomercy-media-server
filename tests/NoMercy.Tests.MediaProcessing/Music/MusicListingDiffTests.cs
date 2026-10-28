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

using NoMercy.MediaProcessing.Music;

namespace NoMercy.Tests.MediaProcessing.Music;

[Trait("Category", "Unit")]
public sealed class MusicListingDiffTests
{
    private static readonly DateTimeOffset Time = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static TrackFileEntry File(string folder, string name, long size, int seconds = 0) =>
        new(folder, "/" + name, size, Time.AddSeconds(seconds));

    [Fact]
    public void A_path_that_vanished_next_to_a_new_path_with_the_same_size_and_time_is_a_move()
    {
        MusicListingDiff.Result diff = MusicListingDiff.Diff(
            [File("/m/A", "one.flac", 100), File("/m/A", "two.flac", 200)],
            [File("/m/B", "one.flac", 100), File("/m/A", "two.flac", 200)]
        );

        diff.Moves.Should().ContainSingle().Which.To.Path.Should().Be("/m/B/one.flac");
        diff.Gone.Should().BeEmpty();
        diff.Added.Should().BeEmpty();
    }

    [Fact]
    public void A_vanished_path_with_no_match_is_gone_and_a_new_path_is_added()
    {
        MusicListingDiff.Result diff = MusicListingDiff.Diff(
            [File("/m/A", "one.flac", 100)],
            [File("/m/A", "new.flac", 999)]
        );

        diff.Moves.Should().BeEmpty();
        diff.Gone.Should().ContainSingle().Which.Path.Should().Be("/m/A/one.flac");
        diff.Added.Should().ContainSingle().Which.Path.Should().Be("/m/A/new.flac");
    }

    [Fact]
    public void Two_files_with_one_identity_are_not_paired()
    {
        MusicListingDiff.Result diff = MusicListingDiff.Diff(
            [File("/m/A", "one.flac", 100), File("/m/A", "two.flac", 100)],
            [File("/m/B", "one.flac", 100), File("/m/B", "two.flac", 100)]
        );

        diff.Moves.Should().BeEmpty();
        diff.Gone.Should().HaveCount(2);
        diff.Added.Should().HaveCount(2);
    }

    [Fact]
    public void A_row_without_a_known_size_is_never_paired_by_size()
    {
        MusicListingDiff.Result diff = MusicListingDiff.Diff(
            [new TrackFileEntry("/m/A", "/one.flac")],
            [File("/m/B", "one.flac", 100)]
        );

        diff.Moves.Should().BeEmpty();
        diff.Gone.Should().HaveCount(1);
    }
}
