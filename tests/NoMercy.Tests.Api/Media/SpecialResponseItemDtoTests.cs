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

using Newtonsoft.Json;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Media;
using NoMercy.Data.DTOs.Specials;
using NoMercy.Database;
using NoMercy.Database.Models.Common;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.NmSystem.Extensions;
using Xunit;

namespace NoMercy.Tests.Api.Media;

[Trait("Category", "Unit")]
public class SpecialResponseItemDtoTests
{
    private static PeopleDto BuildPerson(long id, string name)
    {
        return new()
        {
            Id = id,
            Name = name,
            Gender = "Female",
            Translations = [],
        };
    }

    private static SpecialItemsDto BuildItem(
        int id,
        string mediaType,
        double? voteAverage,
        int totalDuration,
        string ratingIso,
        int[]? episodeIds = null,
        IEnumerable<PeopleDto>? cast = null,
        IEnumerable<PeopleDto>? crew = null
    )
    {
        Movie backing = new() { Id = id, Title = $"Item {id}" };

        return new(backing)
        {
            EpisodeIds = episodeIds ?? [],
            MediaType = mediaType,
            VoteAverage = voteAverage,
            TotalDuration = totalDuration,
            Rating = new() { Iso31661 = ratingIso },
            Cast = cast ?? [],
            Crew = crew ?? [],
            Posters = [new() { Id = id, Src = $"/poster-{id}.jpg" }],
            Backdrops = [new() { Id = id, Src = $"/backdrop-{id}.jpg" }],
            Genres = [new() { Id = id, Name = $"Genre {id}" }],
        };
    }

    [Fact]
    public void Ctor_SpecialWithItems_AggregatesAndSkipsUnmatchedRefs()
    {
        PeopleDto shared = BuildPerson(1, "Shared Person");
        PeopleDto onlyOnTv = BuildPerson(2, "Tv Only Person");

        SpecialItemsDto movieItem = BuildItem(
            201,
            "movie",
            voteAverage: 8.0,
            totalDuration: 100,
            ratingIso: "US",
            cast: [shared]
        );
        SpecialItemsDto tvItem = BuildItem(
            300,
            "tv",
            voteAverage: null,
            totalDuration: 50,
            ratingIso: "US",
            episodeIds: [555, 556],
            cast: [shared, onlyOnTv]
        );
        List<SpecialItemsDto> items = [movieItem, tvItem];

        Ulid specialId = Ulid.NewUlid();
        Special special = new()
        {
            Id = specialId,
            Title = "The Best Of",
            Overview = "ov",
            Backdrop = "https://storage.nomercy.tv/laravel/backdrop.jpg",
            Poster = "/poster.jpg",
            Logo = "/logo.jpg",
        };
        special.SpecialUser.Add(new(specialId, Guid.NewGuid()));

        Movie movieWithFile = new() { Id = 201 };
        movieWithFile.VideoFiles.Add(new() { Filename = "a.mkv", HostFolder = "/x" });

        Episode episode556 = new() { Id = 556 };
        episode556.VideoFiles.Add(new() { Filename = "b.mkv", HostFolder = "/x" });

        Episode episode555 = new() { Id = 555 }; // no video files -> excluded from haveEpisodes

        SpecialItem itemA = new() { MovieId = 201, Movie = movieWithFile };
        itemA.UserData.Add(new() { VideoFileId = Ulid.NewUlid() });

        SpecialItem itemB = new() { EpisodeId = 556, Episode = episode556 };

        SpecialItem itemCUnmatched = new() { EpisodeId = 999 }; // no Episode, no matching item -> skipped

        SpecialItem itemD = new() { EpisodeId = 555, Episode = episode555 }; // duplicate match into tvItem

        special.Items.Add(itemA);
        special.Items.Add(itemB);
        special.Items.Add(itemCUnmatched);
        special.Items.Add(itemD);

        SpecialResponseItemDto dto = new(special, items);

        Assert.Equal(specialId, dto.Id);
        Assert.Equal("The Best Of", dto.Title);
        Assert.Equal("ov", dto.Overview);
        Assert.Equal("/backdrop.jpg", dto.Backdrop);
        Assert.Equal("specials", dto.Type);
        Assert.Equal("specials", dto.MediaType);
        Assert.Equal($"/specials/{specialId}", dto.Link.ToString());
        Assert.Equal("The Best Of".TitleSort(), dto.TitleSort);
        Assert.True(dto.Favorite);

        Assert.Equal(4, dto.NumberOfItems);
        // haveMovies: only itemA (movie has a video file). haveEpisodes: only itemB (episode556 has a file).
        Assert.Equal(2, dto.HaveItems);
        // Not all 4 special items have user data -> not fully watched.
        Assert.False(dto.Watched);

        Assert.Equal(150, dto.TotalDuration); // 100 + 50
        Assert.Equal(8.0, dto.VoteAverage); // only movieItem has a non-null VoteAverage
        dto.ContentRatings.Should().ContainSingle(); // both items share Iso31661 "US"

        // itemB (episode 556) and itemD (episode 555) both resolve to tvItem (Id 300);
        // DistinctBy collapses them together with movieItem (Id 201) into two entries.
        dto.Special.Should().HaveCount(2);
        dto.Special!.Select(i => i.Id).Should().BeEquivalentTo([201, 300]);

        // Cast is de-duplicated by person id across items: shared(1), onlyOnTv(2) -> 2 total.
        dto.Cast.Should().HaveCount(2);
        dto.Posters.Should().HaveCount(2);
        dto.Backdrops.Should().HaveCount(2);
        dto.Genres.Should().HaveCount(2);

        // The source items list is mutated in place: its per-item collections are cleared.
        Assert.Empty(movieItem.Posters);
        Assert.Empty(movieItem.Backdrops);
        Assert.Empty(movieItem.Cast);
        Assert.Empty(movieItem.Genres);
    }

    [Fact]
    public void Ctor_SpecialWithItems_AllItemsHaveUserData_IsWatched()
    {
        SpecialItemsDto movieItem = BuildItem(
            1,
            "movie",
            voteAverage: null,
            totalDuration: 10,
            ratingIso: "US"
        );
        List<SpecialItemsDto> items = [movieItem];

        Special special = new() { Id = Ulid.NewUlid(), Title = "Watched Special" };

        Movie movie = new() { Id = 1 };
        SpecialItem specialItem = new() { MovieId = 1, Movie = movie };
        specialItem.UserData.Add(new() { VideoFileId = Ulid.NewUlid() });
        special.Items.Add(specialItem);

        SpecialResponseItemDto dto = new(special, items);

        Assert.True(dto.Watched);
        Assert.Equal(0, dto.VoteAverage); // no items have a non-null VoteAverage -> Average() is null -> 0
    }

    [Fact]
    public void Ctor_SpecialWithItems_NullBackdropAndNoFavorite_StaysNull()
    {
        Special special = new()
        {
            Id = Ulid.NewUlid(),
            Title = null,
            Backdrop = null,
        };

        SpecialResponseItemDto dto = new(special, []);

        Assert.Equal(string.Empty, dto.Title);
        Assert.Null(dto.Backdrop);
        Assert.False(dto.Favorite);
        Assert.Equal(0, dto.NumberOfItems);
        Assert.Equal(0, dto.HaveItems);
    }

    [Fact]
    public void Ctor_SpecialOnly_ComputesFromRawItemsWithNullSafeMovieNavigation()
    {
        Special special = new() { Id = Ulid.NewUlid(), Title = "Raw Special" };

        Movie movieWithRuntime = new()
        {
            Id = 1,
            Runtime = 100,
            VoteAverage = 7.0,
        };
        movieWithRuntime.VideoFiles.Add(new() { Filename = "a.mkv", HostFolder = "/x" });
        Certification certification = new() { Iso31661 = "US", Rating = "PG" };
        movieWithRuntime.CertificationMovies.Add(
            new()
            {
                CertificationId = 1,
                Certification = certification,
                MovieId = 1,
            }
        );

        Episode episode = new() { Id = 2 };
        episode.VideoFiles.Add(new() { Filename = "b.mkv", HostFolder = "/x" });

        Movie movieWithoutFiles = new()
        {
            Id = 3,
            Runtime = 50,
            VoteAverage = null,
        };

        special.Items.Add(new() { MovieId = 1, Movie = movieWithRuntime });
        special.Items.Add(new() { EpisodeId = 2, Episode = episode }); // no Movie -> null-safe fallbacks
        special.Items.Add(new() { MovieId = 3, Movie = movieWithoutFiles }); // no video files, no certifications

        SpecialResponseItemDto dto = new(special);

        Assert.Equal(3, dto.NumberOfItems);
        Assert.Equal(2, dto.HaveItems); // haveMovies=1 (movieWithRuntime) + haveEpisodes=1 (episode)

        Assert.Empty(dto.Cast);
        Assert.Empty(dto.Crew);
        Assert.Empty(dto.Backdrops);
        Assert.Empty(dto.Posters);
        Assert.Empty(dto.Genres);

        Assert.Equal(150, dto.TotalDuration); // 100 (movie) + 0 (episode item, Movie null) + 50
        Assert.Equal(7.0, dto.VoteAverage); // only movieWithRuntime has a non-null VoteAverage

        // movieWithRuntime -> "US", episode item -> null (no Movie), movieWithoutFiles -> null (no certifications).
        // The two nulls share the same DistinctBy key (null), so distinct count is 2.
        dto.ContentRatings.Should().HaveCount(2);
        Assert.False(dto.Favorite);
    }

    [Fact]
    public void Ctor_SpecialOnly_HaveItemsCountsBothMoviesAndEpisodesWithFiles()
    {
        Special special = new() { Id = Ulid.NewUlid(), Title = "Counts Special" };

        Movie movieWithFile = new() { Id = 1 };
        movieWithFile.VideoFiles.Add(new() { Filename = "a.mkv", HostFolder = "/x" });

        Episode episodeWithFile = new() { Id = 2 };
        episodeWithFile.VideoFiles.Add(new() { Filename = "b.mkv", HostFolder = "/x" });

        special.Items.Add(new() { MovieId = 1, Movie = movieWithFile });
        special.Items.Add(new() { EpisodeId = 2, Episode = episodeWithFile });
        special.SpecialUser.Add(new(special.Id, Guid.NewGuid()));

        SpecialResponseItemDto dto = new(special);

        Assert.Equal(2, dto.HaveItems);
        Assert.True(dto.Favorite);
    }

    private static SpecialDetailDto BuildDetail(string colorPaletteJson)
    {
        return new()
        {
            Id = Ulid.NewUlid(),
            Title = "The Detail Special",
            Overview = "detail overview",
            Backdrop = "https://storage.nomercy.tv/laravel/x.jpg",
            Poster = "/detail-poster.jpg",
            Logo = "/detail-logo.jpg",
            ColorPalette = colorPaletteJson,
            Favorite = true,
            NumberOfItems = 10,
            HaveMovies = 3,
            HaveEpisodes = 4,
            Items =
            [
                new() { MovieId = 401 },
                new() { EpisodeId = 702 },
                new() { EpisodeId = 999 }, // no matching item -> skipped
                new() { EpisodeId = 701 }, // duplicate match into item402
            ],
        };
    }

    [Fact]
    public void Ctor_SpecialDetailDto_UsesDirectFieldsNotComputedCounts()
    {
        string colorPaletteJson = JsonConvert.SerializeObject(
            new ColorPalette { Poster = new() { Dominant = "#123456" } }
        );
        SpecialDetailDto detail = BuildDetail(colorPaletteJson);

        SpecialItemsDto item401 = BuildItem(
            401,
            "movie",
            voteAverage: 9.0,
            totalDuration: 200,
            ratingIso: "US"
        );
        SpecialItemsDto item402 = BuildItem(
            402,
            "tv",
            voteAverage: null,
            totalDuration: 80,
            ratingIso: "NL",
            episodeIds: [701, 702]
        );
        List<SpecialItemsDto> items = [item401, item402];

        SpecialResponseItemDto dto = new(detail, items);

        Assert.Equal(detail.Id, dto.Id);
        Assert.Equal("The Detail Special", dto.Title);
        Assert.Equal("/x.jpg", dto.Backdrop);
        Assert.Equal("The Detail Special".TitleSort(), dto.TitleSort);
        dto.ColorPalette!.Poster!.Dominant.Should().Be("#123456");

        // Favorite/NumberOfItems/HaveItems come straight from the projection, not from `items`.
        Assert.True(dto.Favorite);
        Assert.Equal(10, dto.NumberOfItems);
        Assert.Equal(7, dto.HaveItems); // HaveMovies(3) + HaveEpisodes(4)

        Assert.Equal(280, dto.TotalDuration); // 200 + 80
        Assert.Equal(9.0, dto.VoteAverage);
        dto.ContentRatings.Should().HaveCount(2); // "US" and "NL"

        dto.Special.Should().HaveCount(2);
        dto.Special!.Select(i => i.Id).Should().BeEquivalentTo([401, 402]);
    }

    [Fact]
    public void Ctor_SpecialDetailDto_EmptyColorPaletteAndNotFavorite()
    {
        SpecialDetailDto detail = BuildDetail(string.Empty);
        detail.Favorite = false;

        SpecialResponseItemDto dto = new(detail, []);

        Assert.Null(dto.ColorPalette);
        Assert.False(dto.Favorite);
        Assert.Empty(dto.Special!);
    }
}
