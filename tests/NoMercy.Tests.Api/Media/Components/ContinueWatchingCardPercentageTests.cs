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

using Newtonsoft.Json.Linq;
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.Users;
using Xunit;

namespace NoMercy.Tests.Api.Media.Components;

[Trait("Category", "Unit")]
public class ContinueWatchingCardPercentageTests
{
    private static UserData WatchedMovie(int? time, string? duration)
    {
        Movie movie = new() { Id = 129, Title = "Spirited Away" };

        return new()
        {
            MovieId = 129,
            Movie = movie,
            Time = time,
            VideoFile = new() { Duration = duration },
        };
    }

    [Fact]
    public void PartlyWatchedItem_CarriesHowFarThePlayerGot_AsPercentage()
    {
        JObject card = JObject.FromObject(new CardData(WatchedMovie(1800, "01:00:00"), "US"));

        card.Value<double?>("percentage").Should().BeApproximately(50, 0.001);
    }

    [Fact]
    public void ItemPastItsDuration_IsCappedAtAHundred()
    {
        JObject card = JObject.FromObject(new CardData(WatchedMovie(4000, "01:00:00"), "US"));

        card.Value<double?>("percentage").Should().Be(100);
    }

    [Fact]
    public void ItemWithoutADuration_SendsNoPercentage()
    {
        JObject card = JObject.FromObject(new CardData(WatchedMovie(1800, null), "US"));

        card.ContainsKey("percentage").Should().BeFalse();
    }

    [Fact]
    public void CardThatIsNotContinueWatching_SendsNoPercentage()
    {
        JObject card = JObject.FromObject(
            new CardData(
                new TvCardDto
                {
                    Id = 1399,
                    Title = "Game of Thrones",
                    TitleSort = "game of thrones",
                }
            )
        );

        card.ContainsKey("percentage").Should().BeFalse();
    }
}
