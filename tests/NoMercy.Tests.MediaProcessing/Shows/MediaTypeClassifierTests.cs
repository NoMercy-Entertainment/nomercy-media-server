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
using NoMercy.MediaProcessing.Shows;
using NoMercy.Providers.AniList;
using NoMercy.Providers.AniList.Models;
using NoMercy.Providers.Jikan;
using NoMercy.Providers.Jikan.Models;

namespace NoMercy.Tests.MediaProcessing.Shows;

/// <summary>
/// AniList/Jikan community catalogues list non-Japanese productions that got a
/// fan-run entry (Avatar: The Last Airbender, The Legend of Korra, The Dragon
/// Prince all have real entries), so a title match alone isn't enough —
/// TvShowsController already guarded against this with a Japanese-origin
/// check, but that guard lived only there — every other caller of the shared
/// classifier (the anime/tv audit, new-show onboarding) had no such
/// protection and would misfile a Western co-production into the anime
/// library on title match alone.
/// </summary>
public sealed class MediaTypeClassifierTests
{
    [Fact]
    public async Task ClassifyAsync_TitleMatchesButOriginIsNotJapan_ReturnsTv()
    {
        Mock<IAniListMetadataProvider> aniList = new();
        aniList
            .Setup(p => p.SearchAsync("Avatar: The Last Airbender", 2005, It.IsAny<bool?>()))
            .ReturnsAsync(
                new AniListMedia { Title = new() { Romaji = "Avatar: The Last Airbender" } }
            );
        Mock<IJikanMetadataProvider> jikan = new();

        MediaTypeClassifier classifier = new(aniList.Object, jikan.Object);

        string? result = await classifier.ClassifyAsync("Avatar: The Last Airbender", 2005, ["US"]);

        result.Should().Be("tv");
    }

    [Fact]
    public async Task ClassifyAsync_TitleMatchesAndOriginIsJapan_ReturnsAnime()
    {
        Mock<IAniListMetadataProvider> aniList = new();
        aniList
            .Setup(p => p.SearchAsync("Hunter x Hunter", 2011, It.IsAny<bool?>()))
            .ReturnsAsync(new AniListMedia { Title = new() { Romaji = "Hunter x Hunter" } });
        Mock<IJikanMetadataProvider> jikan = new();

        MediaTypeClassifier classifier = new(aniList.Object, jikan.Object);

        string? result = await classifier.ClassifyAsync("Hunter x Hunter", 2011, ["JP"]);

        result.Should().Be("anime");
    }

    [Fact]
    public async Task ClassifyAsync_OriginUnknown_TrustsTheTitleMatch()
    {
        Mock<IAniListMetadataProvider> aniList = new();
        aniList
            .Setup(p => p.SearchAsync("Hunter x Hunter", 2011, It.IsAny<bool?>()))
            .ReturnsAsync(new AniListMedia { Title = new() { Romaji = "Hunter x Hunter" } });
        Mock<IJikanMetadataProvider> jikan = new();

        MediaTypeClassifier classifier = new(aniList.Object, jikan.Object);

        string? result = await classifier.ClassifyAsync("Hunter x Hunter", 2011);

        result.Should().Be("anime");
    }
}

public class MediaTypeClassifierAniListJikanTests
{
    [Fact]
    public async Task ClassifyAsync_AniListMatchWithJapanOrigin_ReturnsAnime()
    {
        Mock<IAniListMetadataProvider> aniList = new();
        aniList
            .Setup(p => p.SearchAsync("One Piece", 1999, It.IsAny<bool?>()))
            .ReturnsAsync(new AniListMedia { Title = new() { Romaji = "One Piece" } });
        Mock<IJikanMetadataProvider> jikan = new();

        MediaTypeClassifier classifier = new(aniList.Object, jikan.Object);

        string? result = await classifier.ClassifyAsync("One Piece", 1999, ["JP"]);

        result.Should().Be("anime");
        jikan.Verify(
            p => p.SearchAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<bool?>()),
            Times.Never
        );
    }

    [Fact]
    public async Task ClassifyAsync_AniListMatchButNonJapanOrigin_ReturnsTv()
    {
        // Same safety rule as the Kitsu-era check: a title match alone
        // is not enough. TMDB's origin_country is the single source of truth
        // for this check, never a provider's own country field.
        Mock<IAniListMetadataProvider> aniList = new();
        aniList
            .Setup(p => p.SearchAsync("Avatar: The Last Airbender", 2005, It.IsAny<bool?>()))
            .ReturnsAsync(
                new AniListMedia
                {
                    Title = new() { Romaji = "Avatar: The Last Airbender" },
                    CountryOfOrigin = "JP",
                }
            );
        Mock<IJikanMetadataProvider> jikan = new();

        MediaTypeClassifier classifier = new(aniList.Object, jikan.Object);

        string? result = await classifier.ClassifyAsync("Avatar: The Last Airbender", 2005, ["US"]);

        result.Should().Be("tv");
    }

    [Fact]
    public async Task ClassifyAsync_AniListNoMatch_FallsThroughToJikan()
    {
        Mock<IAniListMetadataProvider> aniList = new();
        aniList
            .Setup(p => p.SearchAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<bool?>()))
            .ReturnsAsync((AniListMedia?)null);
        Mock<IJikanMetadataProvider> jikan = new();
        jikan
            .Setup(p => p.SearchAsync("Naruto", 2002, It.IsAny<bool?>()))
            .ReturnsAsync(
                new JikanAnime { Titles = [new() { Type = "Default", Title = "Naruto" }] }
            );

        MediaTypeClassifier classifier = new(aniList.Object, jikan.Object);

        string? result = await classifier.ClassifyAsync("Naruto", 2002, ["JP"]);

        result.Should().Be("anime");
    }

    [Fact]
    public async Task ClassifyAsync_AniListErrors_FallsThroughToJikan_SameAsNoMatch()
    {
        // The classifier must not distinguish "AniList said no" from
        // "AniList's call threw/returned null" for fallthrough purposes.
        Mock<IAniListMetadataProvider> aniList = new();
        aniList
            .Setup(p => p.SearchAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<bool?>()))
            .ThrowsAsync(new HttpRequestException("network error"));
        Mock<IJikanMetadataProvider> jikan = new();
        jikan
            .Setup(p => p.SearchAsync("Naruto", 2002, It.IsAny<bool?>()))
            .ReturnsAsync(
                new JikanAnime { Titles = [new() { Type = "Default", Title = "Naruto" }] }
            );

        MediaTypeClassifier classifier = new(aniList.Object, jikan.Object);

        string? result = await classifier.ClassifyAsync("Naruto", 2002, ["JP"]);

        result.Should().Be("anime");
    }

    [Fact]
    public async Task ClassifyAsync_BothProvidersFail_ReturnsNullNotFalse()
    {
        Mock<IAniListMetadataProvider> aniList = new();
        aniList
            .Setup(p => p.SearchAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<bool?>()))
            .ThrowsAsync(new HttpRequestException("network error"));
        Mock<IJikanMetadataProvider> jikan = new();
        jikan
            .Setup(p => p.SearchAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<bool?>()))
            .ThrowsAsync(new HttpRequestException("network error"));

        MediaTypeClassifier classifier = new(aniList.Object, jikan.Object);

        string? result = await classifier.ClassifyAsync("Some Show", 2020, ["JP"]);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ClassifyAsync_BothProvidersFindZeroCandidates_ReturnsTv()
    {
        Mock<IAniListMetadataProvider> aniList = new();
        aniList
            .Setup(p => p.SearchAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<bool?>()))
            .ReturnsAsync((AniListMedia?)null);
        Mock<IJikanMetadataProvider> jikan = new();
        jikan
            .Setup(p => p.SearchAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<bool?>()))
            .ReturnsAsync((JikanAnime?)null);

        MediaTypeClassifier classifier = new(aniList.Object, jikan.Object);

        string? result = await classifier.ClassifyAsync("Some Western Show", 2020, ["US"]);

        result.Should().Be("tv");
    }
}
