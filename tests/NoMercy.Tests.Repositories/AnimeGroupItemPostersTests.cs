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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Common;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using Xunit;

namespace NoMercy.Tests.Repositories;

// The anime theme, demographic and season cards have no image of their own, so
// each group carries the posters of the titles inside it.
public class AnimeGroupItemPostersTests : IDisposable
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Ulid UserLibrary = Ulid.NewUlid();
    private static readonly Ulid OtherLibrary = Ulid.NewUlid();

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public AnimeGroupItemPostersTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext context = new(_options);
        context.Database.EnsureCreated();

        using SqliteCommand relax = _connection.CreateCommand();
        relax.CommandText = "PRAGMA foreign_keys = OFF;";
        relax.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private int _imageId;

    private Image Poster(string path, string? language, double vote = 5) =>
        new()
        {
            Id = ++_imageId,
            FilePath = path,
            Iso6391 = language,
            Type = "poster",
            VoteAverage = vote,
        };

    private static DateTime Added(int month) => new(2024, month, 1, 0, 0, 0, DateTimeKind.Utc);

    private static VideoFile Video(int? movieId = null, int? episodeId = null) =>
        new()
        {
            Folder = "/anime",
            Filename = $"file-{movieId}-{episodeId}.mkv",
            HostFolder = "/media/anime",
            MovieId = movieId,
            EpisodeId = episodeId,
        };

    // Titles are taken in the order they were added, and only when the user can
    // play them. Popularity is set against that order to prove it is not used.
    // In the user's library: show 1 has a textless poster and a better-rated
    // English one, movie 2 has two textless posters, show 3 has no images at
    // all, and show 5 (added first) has no video file. Show 4 (also added
    // early) sits in a library the user cannot see.
    private async Task SeedAsync(Action<MediaContext> link)
    {
        await using MediaContext context = new(_options);
        context.Libraries.AddRange(
            new Library { Id = UserLibrary, Title = "Anime" },
            new Library { Id = OtherLibrary, Title = "Hidden" }
        );
        context.LibraryUser.Add(new LibraryUser(UserLibrary, UserId));

        context.Episodes.AddRange(
            new Episode
            {
                Id = 11,
                TvId = 1,
                SeasonId = 1,
                EpisodeNumber = 1,
                SeasonNumber = 1,
            },
            new Episode
            {
                Id = 13,
                TvId = 3,
                SeasonId = 3,
                EpisodeNumber = 1,
                SeasonNumber = 1,
            },
            new Episode
            {
                Id = 14,
                TvId = 4,
                SeasonId = 4,
                EpisodeNumber = 1,
                SeasonNumber = 1,
            },
            new Episode
            {
                Id = 15,
                TvId = 5,
                SeasonId = 5,
                EpisodeNumber = 1,
                SeasonNumber = 1,
            }
        );
        context.VideoFiles.AddRange(
            Video(episodeId: 11),
            Video(movieId: 2),
            Video(episodeId: 13),
            Video(episodeId: 14)
        );

        Tv first = new()
        {
            Id = 1,
            Title = "Frieren",
            TitleSort = "frieren",
            Popularity = 10,
            Poster = "/frieren.jpg",
            LibraryId = UserLibrary,
        };
        first.Images.Add(Poster("/frieren-en.jpg", "en", vote: 9));
        first.Images.Add(Poster("/frieren-clean.jpg", null));

        Movie second = new()
        {
            Id = 2,
            Title = "Your Name",
            TitleSort = "your name",
            Popularity = 50,
            Poster = "/your-name.jpg",
            LibraryId = UserLibrary,
        };
        second.Images.Add(Poster("/your-name-clean-low.jpg", null, vote: 3));
        second.Images.Add(Poster("/your-name-clean-high.jpg", null, vote: 8));

        Tv third = new()
        {
            Id = 3,
            Title = "Bocchi the Rock!",
            TitleSort = "bocchi the rock",
            Popularity = 90,
            Poster = "/bocchi.jpg",
            LibraryId = UserLibrary,
        };

        Tv hidden = new()
        {
            Id = 4,
            Title = "Hidden Show",
            TitleSort = "hidden show",
            Popularity = 99,
            Poster = "/hidden.jpg",
            LibraryId = OtherLibrary,
        };

        Tv unplayable = new()
        {
            Id = 5,
            Title = "Not Downloaded",
            TitleSort = "not downloaded",
            Popularity = 99,
            Poster = "/not-downloaded.jpg",
            LibraryId = UserLibrary,
        };

        context.Tvs.AddRange(first, third, hidden, unplayable);
        context.Movies.Add(second);
        link(context);
        await context.SaveChangesAsync();

        // CreatedAt is filled by the database on insert (the moment a title is
        // added), so EF never writes it; set the added dates afterwards.
        foreach ((int id, int month) in new[] { (1, 2), (3, 4), (4, 1), (5, 1) })
            await context
                .Tvs.Where(tv => tv.Id == id)
                .ExecuteUpdateAsync(set => set.SetProperty(tv => tv.CreatedAt, Added(month)));
        await context
            .Movies.Where(movie => movie.Id == 2)
            .ExecuteUpdateAsync(set => set.SetProperty(movie => movie.CreatedAt, Added(3)));
    }

    private static readonly string[] Expected =
    [
        "/frieren-clean.jpg",
        "/your-name-clean-high.jpg",
        "/bocchi.jpg",
    ];

    [Fact]
    public async Task Themes_CarryTheirTitlesPosters_TextlessFirst_FirstAddedFirst_PlayableOnly()
    {
        await SeedAsync(context =>
        {
            context.AnimeThemes.Add(new AnimeTheme { Id = 7, Name = "Music" });
            context.AnimeThemeTv.AddRange(
                new AnimeThemeTv { AnimeThemeId = 7, TvId = 1 },
                new AnimeThemeTv { AnimeThemeId = 7, TvId = 3 },
                new AnimeThemeTv { AnimeThemeId = 7, TvId = 4 },
                new AnimeThemeTv { AnimeThemeId = 7, TvId = 5 }
            );
            context.AnimeThemeMovie.Add(new AnimeThemeMovie { AnimeThemeId = 7, MovieId = 2 });
        });

        await using MediaContext context = new(_options);
        List<AnimeThemeWithCountsDto> themes = await new AnimeThemeRepository(
            context
        ).GetThemesWithCountsAsync(UserId, "en", 10, 0);

        themes.Should().ContainSingle().Which.ItemPosters.Should().Equal(Expected);
    }

    [Fact]
    public async Task Demographics_CarryTheirTitlesPosters_TextlessFirst_FirstAddedFirst_PlayableOnly()
    {
        await SeedAsync(context =>
        {
            context.AnimeDemographics.Add(new AnimeDemographic { Id = 7, Name = "Seinen" });
            context.AnimeDemographicTv.AddRange(
                new AnimeDemographicTv { AnimeDemographicId = 7, TvId = 1 },
                new AnimeDemographicTv { AnimeDemographicId = 7, TvId = 3 },
                new AnimeDemographicTv { AnimeDemographicId = 7, TvId = 4 },
                new AnimeDemographicTv { AnimeDemographicId = 7, TvId = 5 }
            );
            context.AnimeDemographicMovie.Add(
                new AnimeDemographicMovie { AnimeDemographicId = 7, MovieId = 2 }
            );
        });

        await using MediaContext context = new(_options);
        List<AnimeDemographicWithCountsDto> demographics = await new AnimeDemographicRepository(
            context
        ).GetDemographicsWithCountsAsync(UserId, "en", 10, 0);

        demographics.Should().ContainSingle().Which.ItemPosters.Should().Equal(Expected);
    }

    [Fact]
    public async Task Seasons_CarryTheirTitlesPosters_TextlessFirst_FirstAddedFirst_PlayableOnly()
    {
        await SeedAsync(context =>
        {
            context.AnimeSeasons.Add(
                new AnimeSeason
                {
                    Id = 7,
                    Year = 2023,
                    Quarter = "FALL",
                }
            );
            context.AnimeSeasonTv.AddRange(
                new AnimeSeasonTv { AnimeSeasonId = 7, TvId = 1 },
                new AnimeSeasonTv { AnimeSeasonId = 7, TvId = 3 },
                new AnimeSeasonTv { AnimeSeasonId = 7, TvId = 4 },
                new AnimeSeasonTv { AnimeSeasonId = 7, TvId = 5 }
            );
            context.AnimeSeasonMovie.Add(new AnimeSeasonMovie { AnimeSeasonId = 7, MovieId = 2 });
        });

        await using MediaContext context = new(_options);
        List<AnimeSeasonWithCountsDto> seasons = await new AnimeSeasonRepository(
            context
        ).GetSeasonsWithCountsAsync(UserId, 10, 0);

        seasons.Should().ContainSingle().Which.ItemPosters.Should().Equal(Expected);
    }

    [Fact]
    public void Pick_KeepsAtMostNineUniquePosters()
    {
        IEnumerable<GroupPosterRow> rows = Enumerable
            .Range(1, 12)
            .Select(id => new GroupPosterRow(
                GroupId: 1,
                ItemId: id,
                AddedAt: new DateTime(2024, 1, id, 0, 0, 0, DateTimeKind.Utc),
                TitleSort: $"title {id:D2}",
                TextlessPoster: null,
                // Titles 1 and 2 share one poster.
                Poster: id == 2 ? "/1.jpg" : $"/{id}.jpg"
            ));

        GroupItemPosters
            .Pick(rows)[1]
            .Should()
            .Equal(
                "/1.jpg",
                "/3.jpg",
                "/4.jpg",
                "/5.jpg",
                "/6.jpg",
                "/7.jpg",
                "/8.jpg",
                "/9.jpg",
                "/10.jpg"
            );
    }
}
