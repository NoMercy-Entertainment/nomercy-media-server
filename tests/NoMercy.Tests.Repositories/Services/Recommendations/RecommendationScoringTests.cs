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
using NoMercy.Data.Services.Recommendations;
using Xunit;

namespace NoMercy.Tests.Repositories.Services.Recommendations;

[Trait("Category", "Unit")]
public class RecommendationScoringTests
{
    [Fact]
    public void TitleFamily_GroupsTitlesSharingMostOfTheShorterTitle()
    {
        List<string> families = [];

        string first = TitleFamily.Assign("Tom and Jerry: The Movie", families);
        string second = TitleFamily.Assign("Tom and Jerry: Willy Wonka", families);
        string other = TitleFamily.Assign("Ice Age", families);

        second.Should().Be(first);
        other.Should().Be("Ice Age");
        families.Should().Equal("Tom and Jerry: The Movie", "Ice Age");
    }

    [Fact]
    public void MergeCandidates_SameTitleFromTwoLists_AddsSourcesTogether()
    {
        List<RecommendationCandidateDto> merged = RecommendationScoring.MergeCandidates(
            [
                new()
                {
                    MediaId = 1,
                    MediaType = "movie",
                    SourceCount = 1,
                    SourceIds = [10],
                },
            ],
            [
                new()
                {
                    MediaId = 1,
                    MediaType = "movie",
                    SourceCount = 2,
                    SourceIds = [10, 11],
                },
            ],
            [
                new()
                {
                    MediaId = 1,
                    MediaType = "tv",
                    SourceCount = 1,
                    SourceIds = [12],
                },
            ]
        );

        merged.Should().HaveCount(2);
        RecommendationCandidateDto movie = merged.Single(c => c.MediaType == "movie");
        movie.SourceCount.Should().Be(3);
        movie.SourceIds.Should().BeEquivalentTo([10, 11]);
    }

    [Fact]
    public void BuildProfile_NormalisesGenreWeightsAndCollectsFavorites()
    {
        UserAffinityProfile profile = RecommendationScoring.BuildProfile([
            new UserAffinitySourceDto
            {
                ItemId = 129,
                MediaType = "movie",
                Rating = 10,
                IsFavorited = true,
                GenreIds = [16],
            },
            new UserAffinitySourceDto
            {
                ItemId = 1399,
                MediaType = "tv",
                GenreIds = [18],
            },
        ]);

        profile.GenreAffinity[16].Should().Be(1.0);
        profile.GenreAffinity[18].Should().BeApproximately(1.0 / 3.0, 0.0001);
        profile.FavoritedMovieIds.Should().Equal(129);
        profile.FavoritedTvIds.Should().BeEmpty();
        profile.SourceItems.Keys.Should().BeEquivalentTo([129, 1399]);
    }

    [Fact]
    public void SelectWithDiversity_GivesEveryTypeItsFloorBeforeTheBestScores()
    {
        List<(string Type, double Score)> scored =
        [
            ("movie", 9),
            ("movie", 8),
            ("movie", 7),
            ("tv", 1),
        ];

        List<(string Type, double Score)> picked = RecommendationScoring.SelectWithDiversity(
            scored,
            take: 2,
            item => item.Type,
            item => item.Score
        );

        picked.Should().Equal(("movie", 9), ("tv", 1));
    }
}
