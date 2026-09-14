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

namespace NoMercy.Tests.NmSystem;

[Trait("Category", "Unit")]
public class AlphaBucketTests
{
    [Fact]
    public void Buckets_Contains27Elements()
    {
        AlphaBucket.Buckets.Should().HaveCount(27);
    }

    [Fact]
    public void Buckets_StartsWithHash()
    {
        AlphaBucket.Buckets[0].Should().Be("#");
    }

    [Fact]
    public void Buckets_ContainsAllLettersAToZ()
    {
        const string letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        for (int i = 1; i < AlphaBucket.Buckets.Length; i++)
        {
            AlphaBucket.Buckets[i].Should().Be(letters[i - 1].ToString());
        }
    }

    [Theory]
    [InlineData([null, "#", true])]
    [InlineData(["", "#", true])]
    [InlineData(["  ", "#", true])]
    public void Matches_EmptyTitleSort_MapsToHash(string? titleSort, string bucket, bool expected)
    {
        AlphaBucket.Matches(titleSort, bucket).Should().Be(expected);
    }

    [Theory]
    [InlineData(["apple", "A", true])]
    [InlineData(["apple", "B", false])]
    [InlineData(["alice", "A", true])]
    [InlineData(["zebra", "Z", true])]
    [InlineData(["movie", "M", true])]
    public void Matches_LetterBucket_CaseInsensitive(string titleSort, string bucket, bool expected)
    {
        AlphaBucket.Matches(titleSort, bucket).Should().Be(expected);
    }

    [Theory]
    [InlineData(["Apple", "A", true])]
    [InlineData(["APPLE", "A", true])]
    [InlineData(["aPpLe", "A", true])]
    public void Matches_LetterBucket_IgnoresCase(string titleSort, string bucket, bool expected)
    {
        AlphaBucket.Matches(titleSort, bucket).Should().Be(expected);
    }

    [Theory]
    [InlineData(["1234", "#", true])]
    [InlineData(["1234", "A", false])]
    [InlineData(["#hashtag", "#", true])]
    [InlineData(["@mention", "#", true])]
    [InlineData(["[bracket", "#", true])]
    public void Matches_NonLetterStartingTitle_MapsToHash(
        string titleSort,
        string bucket,
        bool expected
    )
    {
        AlphaBucket.Matches(titleSort, bucket).Should().Be(expected);
    }

    [Theory]
    [InlineData(["_underscore", "#", true])]
    [InlineData(["-dash", "#", true])]
    [InlineData(["(paren", "#", true])]
    public void Matches_SpecialCharacterStart_MapsToHash(
        string titleSort,
        string bucket,
        bool expected
    )
    {
        AlphaBucket.Matches(titleSort, bucket).Should().Be(expected);
    }

    [Fact]
    public void Matches_HashBucket_OnlyMatchesNonLetterStarts()
    {
        AlphaBucket.Matches("apple", "#").Should().BeFalse();
        AlphaBucket.Matches("123abc", "#").Should().BeTrue();
        AlphaBucket.Matches("_apple", "#").Should().BeTrue();
        AlphaBucket.Matches(null, "#").Should().BeTrue();
    }

    [Theory]
    [InlineData([null, "#"])]
    [InlineData(["", "#"])]
    [InlineData(["abba", "A"])]
    [InlineData(["Queen", "Q"])]
    [InlineData(["2Pac", "#"])]
    [InlineData(["Édith Piaf", "#"])]
    public void LetterFor_TakesTheFirstAsciiLetterOrHash(string? name, string expected)
    {
        AlphaBucket.LetterFor(name).Should().Be(expected);
    }
}
