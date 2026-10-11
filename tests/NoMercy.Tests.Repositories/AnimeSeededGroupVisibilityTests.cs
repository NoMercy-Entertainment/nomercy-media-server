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

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Common;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.TvShows;

namespace NoMercy.Tests.Repositories;

/// <summary>
/// The theme and demographic seed adds every name, linked or not. A group with
/// no title the user can see must stay out of the lists and the carousels, or a
/// card opens an empty page.
/// </summary>
public class AnimeSeededGroupVisibilityTests : IDisposable
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Ulid UserLibrary = Ulid.NewUlid();
    private static readonly Ulid OtherLibrary = Ulid.NewUlid();
    private static readonly DateOnly Today = new(2026, 10, 6);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public AnimeSeededGroupVisibilityTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext context = new(_options);
        context.Database.EnsureCreated();

        using SqliteCommand relax = _connection.CreateCommand();
        relax.CommandText = "PRAGMA foreign_keys = OFF;";
        relax.ExecuteNonQuery();

        Seed(context);
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    // Themes 1 and 11 are linked to a playable show the user can see. Themes 2 and 12
    // are seeded with no link. Themes 3 and 13 link only to a show in a library the
    // user cannot see.
    private static void Seed(MediaContext context)
    {
        context.Libraries.AddRange(
            new Library { Id = UserLibrary, Title = "Anime" },
            new Library { Id = OtherLibrary, Title = "Hidden" }
        );
        context.LibraryUser.Add(new LibraryUser(UserLibrary, UserId));

        context.Tvs.AddRange(
            new Tv
            {
                Id = 1,
                Title = "Frieren",
                TitleSort = "frieren",
                LibraryId = UserLibrary,
            },
            new Tv
            {
                Id = 2,
                Title = "Hidden show",
                TitleSort = "hidden show",
                LibraryId = OtherLibrary,
            }
        );
        context.Episodes.Add(
            new Episode
            {
                Id = 11,
                TvId = 1,
                SeasonId = 1,
                EpisodeNumber = 1,
                SeasonNumber = 1,
            }
        );
        context.VideoFiles.Add(
            new VideoFile
            {
                Folder = "/anime",
                Filename = "frieren.mkv",
                HostFolder = "/media/anime",
                EpisodeId = 11,
            }
        );

        context.AnimeThemes.AddRange(
            new AnimeTheme { Id = 1, Name = "Isekai" },
            new AnimeTheme { Id = 2, Name = "Gore" },
            new AnimeTheme { Id = 3, Name = "Space" }
        );
        context.AnimeThemeTv.AddRange(
            new AnimeThemeTv { AnimeThemeId = 1, TvId = 1 },
            new AnimeThemeTv { AnimeThemeId = 3, TvId = 2 }
        );

        context.AnimeDemographics.AddRange(
            new AnimeDemographic { Id = 11, Name = "Shounen" },
            new AnimeDemographic { Id = 12, Name = "Seinen" },
            new AnimeDemographic { Id = 13, Name = "Josei" }
        );
        context.AnimeDemographicTv.AddRange(
            new AnimeDemographicTv { AnimeDemographicId = 11, TvId = 1 },
            new AnimeDemographicTv { AnimeDemographicId = 13, TvId = 2 }
        );

        context.SaveChanges();
    }

    [Fact]
    public async Task Themes_List_LeavesOutThemesWithNoVisibleTitle()
    {
        await using MediaContext context = new(_options);

        List<AnimeThemeWithCountsDto> themes = await new AnimeThemeRepository(
            context
        ).GetThemesWithCountsAsync(UserId, "en", 50, 0);

        Assert.Equal(["Isekai"], themes.Select(t => t.Name));
        Assert.Equal(1, themes[0].TvShowsWithVideo);
    }

    [Fact]
    public async Task Themes_List_CarriesTheThemeDescription()
    {
        await using MediaContext context = new(_options);
        await context
            .AnimeThemes.Where(t => t.Id == 1)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Description, "Another world."));

        List<AnimeThemeWithCountsDto> themes = await new AnimeThemeRepository(
            context
        ).GetThemesWithCountsAsync(UserId, "en", 50, 0);

        Assert.Equal("Another world.", Assert.Single(themes).Description);
    }

    [Fact]
    public async Task Themes_ByIds_LeavesOutThemesWithNoVisibleTitle()
    {
        await using MediaContext context = new(_options);

        List<AnimeThemeWithCountsDto> themes = await new AnimeThemeRepository(
            context
        ).GetThemesWithCountsByIdsAsync(UserId, "en", [1, 2, 3]);

        Assert.Equal([1], themes.Select(t => t.Id));
    }

    [Theory]
    [InlineData(AnimeCarouselOrder.RandomDaily)]
    [InlineData(AnimeCarouselOrder.Newest)]
    [InlineData(AnimeCarouselOrder.MostTitles)]
    [InlineData(AnimeCarouselOrder.MostWatched)]
    public async Task Themes_Carousel_LeavesOutThemesWithNoVisibleTitle(AnimeCarouselOrder order)
    {
        await using MediaContext context = new(_options);

        List<int> ids = await new AnimeThemeRepository(context).GetTopThemeIdsAsync(
            UserId,
            order,
            Today,
            10
        );

        Assert.DoesNotContain(2, ids);
        Assert.DoesNotContain(3, ids);
    }

    [Fact]
    public async Task Demographics_List_LeavesOutDemographicsWithNoVisibleTitle()
    {
        await using MediaContext context = new(_options);

        List<AnimeDemographicWithCountsDto> demographics = await new AnimeDemographicRepository(
            context
        ).GetDemographicsWithCountsAsync(UserId, "en", 50, 0);

        Assert.Equal(["Shounen"], demographics.Select(d => d.Name));
        Assert.Equal(1, demographics[0].TvShowsWithVideo);
    }

    [Fact]
    public async Task Demographics_ByIds_LeavesOutDemographicsWithNoVisibleTitle()
    {
        await using MediaContext context = new(_options);

        List<AnimeDemographicWithCountsDto> demographics = await new AnimeDemographicRepository(
            context
        ).GetDemographicsWithCountsByIdsAsync(UserId, "en", [11, 12, 13]);

        Assert.Equal([11], demographics.Select(d => d.Id));
    }

    [Theory]
    [InlineData(AnimeCarouselOrder.RandomDaily)]
    [InlineData(AnimeCarouselOrder.Newest)]
    [InlineData(AnimeCarouselOrder.MostTitles)]
    [InlineData(AnimeCarouselOrder.MostWatched)]
    public async Task Demographics_Carousel_LeavesOutDemographicsWithNoVisibleTitle(
        AnimeCarouselOrder order
    )
    {
        await using MediaContext context = new(_options);

        List<int> ids = await new AnimeDemographicRepository(context).GetTopDemographicIdsAsync(
            UserId,
            order,
            Today,
            10
        );

        Assert.DoesNotContain(12, ids);
        Assert.DoesNotContain(13, ids);
    }
}
