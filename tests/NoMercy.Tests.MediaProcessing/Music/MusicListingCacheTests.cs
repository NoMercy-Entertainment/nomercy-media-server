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

using Moq;
using NoMercy.MediaProcessing.Music;
using NoMercy.Storage;

namespace NoMercy.Tests.MediaProcessing.Music;

/// <summary>A folder whose own last-write time is unchanged is not enumerated again.</summary>
[Trait("Category", "Unit")]
public sealed class MusicListingCacheTests
{
    private static readonly DateTime Written = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IStorageDriver> _driver = new();
    private DateTime _albumWritten = Written;
    private long _fileSize = 10;

    public MusicListingCacheTests()
    {
        _driver.Setup(d => d.GetLastWriteTimeUtc("/m")).Returns(Written);
        _driver.Setup(d => d.GetLastWriteTimeUtc("/m/Album")).Returns(() => _albumWritten);
        _driver
            .Setup(d => d.EnumerateEntries("/m", "*", SearchOption.TopDirectoryOnly))
            .Returns([new StorageEntryInfo("/m/Album", true, 0, Written)]);
        _driver
            .Setup(d => d.EnumerateEntries("/m/Album", "*", SearchOption.TopDirectoryOnly))
            .Returns(() => [new StorageEntryInfo("/m/Album/one.flac", false, _fileSize, Written)]);
    }

    [Fact]
    public void A_forced_listing_sees_a_file_edited_in_place_inside_an_unchanged_folder()
    {
        MusicListingCache cache = new();
        cache.List(_driver.Object, "/m");
        _fileSize = 99;

        (IReadOnlyList<TrackFileEntry> previous, IReadOnlyList<TrackFileEntry> current) =
            cache.List(_driver.Object, "/m", force: true);

        current.Should().ContainSingle().Which.SizeBytes.Should().Be(99);
        previous.Should().ContainSingle().Which.SizeBytes.Should().Be(10);
    }

    [Fact]
    public void An_automatic_listing_still_reuses_the_cached_folder()
    {
        MusicListingCache cache = new();
        cache.List(_driver.Object, "/m");
        _fileSize = 99;

        (_, IReadOnlyList<TrackFileEntry> current) = cache.List(_driver.Object, "/m");

        current.Should().ContainSingle().Which.SizeBytes.Should().Be(10);
    }

    [Fact]
    public void An_unchanged_folder_is_not_enumerated_a_second_time()
    {
        MusicListingCache cache = new();
        cache.List(_driver.Object, "/m");

        (IReadOnlyList<TrackFileEntry> previous, IReadOnlyList<TrackFileEntry> current) =
            cache.List(_driver.Object, "/m");

        _driver.Verify(
            d => d.EnumerateEntries("/m", "*", SearchOption.TopDirectoryOnly),
            Times.Once
        );
        _driver.Verify(
            d => d.EnumerateEntries("/m/Album", "*", SearchOption.TopDirectoryOnly),
            Times.Once
        );
        current.Should().ContainSingle().Which.Path.Should().Be("/m/Album/one.flac");
        previous.Should().BeEquivalentTo(current);
    }

    [Fact]
    public void A_folder_written_since_the_last_listing_is_enumerated_again_and_its_neighbors_are_not()
    {
        MusicListingCache cache = new();
        cache.List(_driver.Object, "/m");
        _albumWritten = Written.AddMinutes(1);

        cache.List(_driver.Object, "/m");

        _driver.Verify(
            d => d.EnumerateEntries("/m/Album", "*", SearchOption.TopDirectoryOnly),
            Times.Exactly(2)
        );
        _driver.Verify(
            d => d.EnumerateEntries("/m", "*", SearchOption.TopDirectoryOnly),
            Times.Once
        );
    }

    [Fact]
    public void A_folder_listed_within_the_clock_tick_of_its_last_change_is_listed_again()
    {
        // The folder changed 0.5 s before the first listing: a change in that same tick
        // leaves its time as it is, so the first listing cannot be trusted later.
        MusicListingCache cache = new(new FixedClock(Written.AddMilliseconds(500)));
        cache.List(_driver.Object, "/m");
        _fileSize = 99;

        (_, IReadOnlyList<TrackFileEntry> current) = cache.List(_driver.Object, "/m");

        current.Should().ContainSingle().Which.SizeBytes.Should().Be(99);
    }

    [Fact]
    public void A_folder_listed_well_after_its_last_change_is_reused()
    {
        MusicListingCache cache = new(new FixedClock(Written.AddMinutes(5)));
        cache.List(_driver.Object, "/m");
        _fileSize = 99;

        (_, IReadOnlyList<TrackFileEntry> current) = cache.List(_driver.Object, "/m");

        current.Should().ContainSingle().Which.SizeBytes.Should().Be(10);
    }

    private sealed class FixedClock(DateTime nowUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(nowUtc);
    }
}
