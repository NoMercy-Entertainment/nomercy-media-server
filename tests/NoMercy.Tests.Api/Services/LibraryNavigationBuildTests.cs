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
using NoMercy.Api.DTOs.Media;
using NoMercy.Api.Services;
using NoMercy.Database.Models.Libraries;
using Xunit;

namespace NoMercy.Tests.Api.Services;

[Trait("Category", "Unit")]
public class LibraryNavigationBuildTests
{
    private static readonly string[] MusicIds =
    [
        "MusicStart",
        "MusicArtists",
        "MusicAlbums",
        "MusicGenres",
        "MusicFavorites",
    ];

    private static readonly LibraryNavigationEntryDto VideoPlugin = new()
    {
        Id = "plugin-video",
        Origin = LibraryNavigationOrigin.Plugin,
    };

    private static readonly LibraryNavigationEntryDto MusicPlugin = new()
    {
        Id = "plugin-music",
        Origin = LibraryNavigationOrigin.Plugin,
    };

    private static Library Library(string type, int order, string title = "Lib") =>
        new()
        {
            Id = Ulid.NewUlid(),
            Type = type,
            Order = order,
            Title = title,
        };

    private static string[] Ids(List<LibraryNavigationEntryDto> entries) =>
        [.. entries.Select(entry => entry.Id)];

    [Fact]
    public void MusicOnly_OffersNoVideoPages()
    {
        List<LibraryNavigationEntryDto> entries = LibraryNavigation.Build(
            [Library("music", 1)],
            hasAnime: true,
            [VideoPlugin],
            [MusicPlugin]
        );

        Ids(entries).Should().Equal(["plugin-video", .. MusicIds, "plugin-music"]);
    }

    [Fact]
    public void MoviesAndShows_ListLibrariesByOrderThenPagesThenMusic()
    {
        Library shows = Library("tv", 2, "Shows");
        Library movies = Library("movie", 1, "Movies");

        List<LibraryNavigationEntryDto> entries = LibraryNavigation.Build(
            [shows, movies],
            hasAnime: false,
            [VideoPlugin],
            [MusicPlugin]
        );

        Ids(entries)
            .Should()
            .Equal([
                movies.Id.ToString(),
                shows.Id.ToString(),
                "collections",
                "specials",
                "genres",
                "people",
                "favorites",
                "lists",
                "plugin-video",
                .. MusicIds,
                "plugin-music",
            ]);
        entries[0].Icon.Should().Be("movieClap");
        entries[0].Link.Should().Be($"/libraries/{movies.Id}");
        entries[1].Icon.Should().Be("monitor");
    }

    [Fact]
    public void ShowsWithAnime_AddAnimePagesButNoCollections()
    {
        List<LibraryNavigationEntryDto> entries = LibraryNavigation.Build(
            [Library("anime", 1)],
            hasAnime: true,
            [],
            []
        );

        string[] ids = Ids(entries);
        ids.Should().NotContain("collections");
        ids.Should()
            .ContainInOrder(
                "lists",
                "anime-themes",
                "anime-demographics",
                "anime-seasons",
                "MusicStart"
            );
    }

    [Fact]
    public void UnknownLibraryType_DrawsTheFolderIcon()
    {
        LibraryNavigation.Build([Library("other", 1)], false, [], [])[0].Icon.Should().Be("folder");
    }

    [Fact]
    public void MusicPages_KeepTheirIconsAndRouteType()
    {
        List<LibraryNavigationEntryDto> music =
        [
            .. LibraryNavigation
                .Build([], false, [], [])
                .Where(entry => entry.RouteType == "music"),
        ];

        music
            .Select(entry => entry.Icon)
            .Should()
            .Equal("folder", "speaker", "disk", "noteClefTreble", "heart");
        music.Should().OnlyContain(entry => entry.Origin == LibraryNavigationOrigin.Page);
    }
}
