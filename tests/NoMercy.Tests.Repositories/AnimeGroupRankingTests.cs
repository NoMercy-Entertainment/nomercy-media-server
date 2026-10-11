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
using NoMercy.Data.Repositories;

namespace NoMercy.Tests.Repositories;

// Which anime groups a carousel shows first: the ordering modes, the daily
// shuffle, and the fallback for an unknown mode name.
public class AnimeGroupRankingTests
{
    private static readonly DateOnly Today = new(2026, 10, 6);

    private static GroupRankRow Row(
        int id,
        string title,
        int playable = 1,
        DateTime? newest = null,
        int watched = 0,
        int year = 0,
        int quarter = 0
    ) => new(id, title, playable, newest, watched, year, quarter);

    private static DateTime Added(int month) => new(2024, month, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Newest_Seasons_OrdersByYearThenQuarterDescending()
    {
        List<GroupRankRow> rows =
        [
            Row(1, "a", year: 2023, quarter: 4),
            Row(2, "b", year: 2024, quarter: 1),
            Row(3, "c", year: 2024, quarter: 3),
            Row(4, "d", year: 2022, quarter: 2),
        ];

        AnimeGroupRanking
            .TopIds(rows, AnimeCarouselOrder.Newest, AnimeGroupKind.Season, Today, 10)
            .Should()
            .Equal(3, 2, 1, 4);
    }

    [Fact]
    public void Newest_Themes_OrdersByNewestAddedTitleDescending()
    {
        List<GroupRankRow> rows =
        [
            Row(1, "a", newest: Added(2)),
            Row(2, "b", newest: Added(5)),
            Row(3, "c", newest: Added(3)),
        ];

        AnimeGroupRanking
            .TopIds(rows, AnimeCarouselOrder.Newest, AnimeGroupKind.Theme, Today, 10)
            .Should()
            .Equal(2, 3, 1);
    }

    [Fact]
    public void MostTitles_OrdersByPlayableCountThenTitle()
    {
        List<GroupRankRow> rows =
        [
            Row(1, "zeta", playable: 3),
            Row(2, "alpha", playable: 3),
            Row(3, "mid", playable: 7),
            Row(4, "low", playable: 1),
        ];

        AnimeGroupRanking
            .TopIds(rows, AnimeCarouselOrder.MostTitles, AnimeGroupKind.Theme, Today, 10)
            .Should()
            .Equal(3, 2, 1, 4);
    }

    [Fact]
    public void MostWatched_OrdersByWatchedCountThenMostTitles()
    {
        List<GroupRankRow> rows =
        [
            Row(1, "a", playable: 2, watched: 1),
            Row(2, "b", playable: 9, watched: 0),
            Row(3, "c", playable: 5, watched: 1),
            Row(4, "d", playable: 1, watched: 4),
        ];

        AnimeGroupRanking
            .TopIds(rows, AnimeCarouselOrder.MostWatched, AnimeGroupKind.Theme, Today, 10)
            .Should()
            .Equal(4, 3, 1, 2);
    }

    [Theory]
    [InlineData(AnimeCarouselOrder.RandomDaily)]
    [InlineData(AnimeCarouselOrder.Newest)]
    [InlineData(AnimeCarouselOrder.MostTitles)]
    [InlineData(AnimeCarouselOrder.MostWatched)]
    public void AGroupWithNoPlayableTitle_IsLeftOut(AnimeCarouselOrder order)
    {
        List<GroupRankRow> rows =
        [
            Row(1, "a", playable: 0, newest: Added(9), watched: 5, year: 2030, quarter: 4),
            Row(2, "b", playable: 1, newest: Added(1), year: 2000, quarter: 1),
        ];

        AnimeGroupRanking.TopIds(rows, order, AnimeGroupKind.Theme, Today, 10).Should().Equal(2);
    }

    [Fact]
    public void Take_KeepsOnlyTheFirstGroups()
    {
        List<GroupRankRow> rows = [.. Enumerable.Range(1, 8).Select(id => Row(id, $"t{id}", id))];

        AnimeGroupRanking
            .TopIds(rows, AnimeCarouselOrder.MostTitles, AnimeGroupKind.Theme, Today, 3)
            .Should()
            .Equal(8, 7, 6);
    }

    private static List<GroupRankRow> Twenty() =>
        [.. Enumerable.Range(1, 20).Select(id => Row(id, $"t{id}"))];

    [Fact]
    public void RandomDaily_IsTheSameTwiceOnOneDay()
    {
        List<int> first = AnimeGroupRanking.TopIds(
            Twenty(),
            AnimeCarouselOrder.RandomDaily,
            AnimeGroupKind.Theme,
            Today,
            20
        );
        List<int> second = AnimeGroupRanking.TopIds(
            Twenty(),
            AnimeCarouselOrder.RandomDaily,
            AnimeGroupKind.Theme,
            Today,
            20
        );

        second.Should().Equal(first);
        first.Should().BeEquivalentTo(Enumerable.Range(1, 20));
    }

    [Fact]
    public void RandomDaily_DiffersOnAnotherDay()
    {
        List<int> today = AnimeGroupRanking.TopIds(
            Twenty(),
            AnimeCarouselOrder.RandomDaily,
            AnimeGroupKind.Theme,
            Today,
            20
        );
        List<int> tomorrow = AnimeGroupRanking.TopIds(
            Twenty(),
            AnimeCarouselOrder.RandomDaily,
            AnimeGroupKind.Theme,
            Today.AddDays(1),
            20
        );

        tomorrow.Should().NotEqual(today);
    }

    [Fact]
    public void RandomDaily_DiffersPerGroupType()
    {
        List<int> themes = AnimeGroupRanking.TopIds(
            Twenty(),
            AnimeCarouselOrder.RandomDaily,
            AnimeGroupKind.Theme,
            Today,
            20
        );
        List<int> demographics = AnimeGroupRanking.TopIds(
            Twenty(),
            AnimeCarouselOrder.RandomDaily,
            AnimeGroupKind.Demographic,
            Today,
            20
        );

        demographics.Should().NotEqual(themes);
    }

    [Theory]
    [InlineData("random_daily", AnimeCarouselOrder.RandomDaily)]
    [InlineData("newest", AnimeCarouselOrder.Newest)]
    [InlineData("most_titles", AnimeCarouselOrder.MostTitles)]
    [InlineData("most_watched", AnimeCarouselOrder.MostWatched)]
    [InlineData("  Most_Titles ", AnimeCarouselOrder.MostTitles)]
    public void Parse_KnownName_GivesThatMode(string value, AnimeCarouselOrder expected)
    {
        AnimeCarouselOrderParser.Parse(value, AnimeGroupKind.Season).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sideways")]
    public void Parse_UnknownOrMissing_FallsBackPerGroupType(string? value)
    {
        AnimeCarouselOrderParser
            .Parse(value, AnimeGroupKind.Season)
            .Should()
            .Be(AnimeCarouselOrder.Newest);
        AnimeCarouselOrderParser
            .Parse(value, AnimeGroupKind.Theme)
            .Should()
            .Be(AnimeCarouselOrder.RandomDaily);
        AnimeCarouselOrderParser
            .Parse(value, AnimeGroupKind.Demographic)
            .Should()
            .Be(AnimeCarouselOrder.RandomDaily);
    }
}
