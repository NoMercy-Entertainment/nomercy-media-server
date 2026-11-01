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

using MovieFileLibrary;

namespace NoMercy.Tests.MediaProcessing.Parsing;

/// <summary>
/// Behavioural corpus for the vendored <see cref="MovieDetector"/> (formerly the
/// MovieFileLibrary NuGet package, now owned in-tree with the five inline regexes
/// promoted to named <c>GeneratedRegex</c>). These cases pin the upstream
/// token-loop algorithm so the vendoring is proven to preserve behaviour.
/// </summary>
public class VendoredMovieDetectorTests
{
    // filename, title, year, season(-1 = null), episode(-1 = null), isSeries, isSpecial
    [Theory]
    [InlineData([
        "Inception.2010.1080p.BluRay.x264.mkv",
        "Inception",
        "2010",
        -1,
        -1,
        false,
        false,
    ])]
    [InlineData(["Normal.People.S01E04.1080p.mkv", "Normal People", null, 1, 4, true, false])]
    [InlineData(["The.Grand.Tour.S04.E04.1080p.mkv", "The Grand Tour", null, 4, 4, true, false])]
    [InlineData(["Top Gear 17x03 HDTV.mp4", "Top Gear", null, 17, 3, true, false])]
    [InlineData([
        "Scenes.from.a.Marriage.1973.E01.mkv",
        "Scenes from a Marriage",
        "1973",
        -1,
        1,
        true,
        false,
    ])]
    [InlineData([
        "The.Legend.of.1900.1998.mkv",
        "The Legend of 1900",
        "1998",
        -1,
        -1,
        false,
        false,
    ])]
    [InlineData(["Sherlock.S01.Special.mkv", "Sherlock", null, 1, -1, true, true])]
    public void GetInfo_ParsesCanonicalCorpus(
        string fileName,
        string expectedTitle,
        string? expectedYear,
        int expectedSeason,
        int expectedEpisode,
        bool expectedIsSeries,
        bool expectedIsSpecial
    )
    {
        MovieFile result = new MovieDetector().GetInfo(fileName);

        result.IsSuccess.Should().BeTrue();
        result.Title.Should().Be(expectedTitle);
        result.Year.Should().Be(expectedYear);
        result.IsSeries.Should().Be(expectedIsSeries);
        result.IsSpecialEpisode.Should().Be(expectedIsSpecial);

        if (expectedSeason < 0)
            result.Season.Should().BeNull();
        else
            result.Season.Should().Be(expectedSeason);

        if (expectedEpisode < 0)
            result.Episode.Should().BeNull();
        else
            result.Episode.Should().Be(expectedEpisode);
    }

    [Fact]
    public void GetInfo_ExtractsImdbIdAfterYear()
    {
        MovieFile result = new MovieDetector().GetInfo("Batman Begins (2005) {imdb-tt0372784}.mkv");

        result.Title.Should().Be("Batman Begins");
        result.Year.Should().Be("2005");
        result.ImdbId.Should().Be("tt0372784");
        result.IsSeries.Should().BeFalse();
    }

    [Fact]
    public void Episode_SetterKeepsEpisodesCollectionConsistent()
    {
        MovieFile file = new("Show.S01E02.mkv") { Episode = 7 };

        file.Episode.Should().Be(7);
        file.Episodes.Should().ContainSingle().Which.Should().Be(7);
    }

    [Fact]
    public void GetInfo_Throws_OnNullOrWhitespace()
    {
        Action act = () => new MovieDetector().GetInfo("   ");
        act.Should().Throw<ArgumentException>();
    }
}
