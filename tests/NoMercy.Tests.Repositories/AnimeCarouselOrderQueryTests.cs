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
using NoMercy.Database.Models.TvShows;
using NoMercy.Database.Models.Users;

namespace NoMercy.Tests.Repositories;

// The light "which groups show first" queries of the anime repositories, on
// seeded data: playability, library access, the added dates, and whose watches
// count. The mode ordering itself is covered by AnimeGroupRankingTests.
public class AnimeCarouselOrderQueryTests : IDisposable
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid OtherUserId = Guid.NewGuid();
    private static readonly Ulid UserLibrary = Ulid.NewUlid();
    private static readonly Ulid OtherLibrary = Ulid.NewUlid();
    private static readonly DateOnly Today = new(2026, 10, 6);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public AnimeCarouselOrderQueryTests()
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

    private static DateTime Added(int month) => new(2024, month, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Tv Show(int id, string title, Ulid library) =>
        new()
        {
            Id = id,
            Title = title,
            TitleSort = title.ToLowerInvariant(),
            Poster = $"/{id}.jpg",
            LibraryId = library,
        };

    // Shows 1 (added Jan), 3 (Feb) and 2 (Mar) are playable in the user's
    // library. Show 4 has no video file. Show 5 is playable, but in a library
    // the user cannot see.
    private async Task SeedTitlesAsync(Action<MediaContext> link)
    {
        await using MediaContext context = new(_options);
        context.Libraries.AddRange(
            new Library { Id = UserLibrary, Title = "Anime" },
            new Library { Id = OtherLibrary, Title = "Hidden" }
        );
        context.LibraryUser.AddRange(
            new LibraryUser(UserLibrary, UserId),
            new LibraryUser(UserLibrary, OtherUserId)
        );

        foreach (int id in new[] { 1, 2, 3, 4, 5 })
            context.Episodes.Add(
                new Episode
                {
                    Id = 10 + id,
                    TvId = id,
                    SeasonId = id,
                    EpisodeNumber = 1,
                    SeasonNumber = 1,
                }
            );
        foreach (int id in new[] { 1, 2, 3, 5 })
            context.VideoFiles.Add(
                new VideoFile
                {
                    Folder = "/anime",
                    Filename = $"file-{id}.mkv",
                    HostFolder = "/media/anime",
                    EpisodeId = 10 + id,
                }
            );

        context.Tvs.AddRange(
            Show(1, "One", UserLibrary),
            Show(2, "Two", UserLibrary),
            Show(3, "Three", UserLibrary),
            Show(4, "Four", UserLibrary),
            Show(5, "Five", OtherLibrary)
        );
        link(context);
        await context.SaveChangesAsync();

        // CreatedAt is filled by the database on insert, so EF never writes it.
        foreach ((int id, int month) in new[] { (1, 1), (3, 2), (2, 3), (4, 6), (5, 6) })
            await context
                .Tvs.Where(tv => tv.Id == id)
                .ExecuteUpdateAsync(set => set.SetProperty(tv => tv.CreatedAt, Added(month)));
    }

    private async Task WatchAsync(Guid userId, params int[] tvIds)
    {
        await using MediaContext context = new(_options);
        foreach (int tvId in tvIds)
            context.UserData.Add(
                new UserData
                {
                    UserId = userId,
                    TvId = tvId,
                    VideoFileId = Ulid.NewUlid(),
                    Type = "tv",
                }
            );
        await context.SaveChangesAsync();
    }

    private async Task<List<int>> TopThemeIdsAsync(AnimeCarouselOrder order, DateOnly? day = null)
    {
        await using MediaContext context = new(_options);
        return await new AnimeThemeRepository(context).GetTopThemeIdsAsync(
            UserId,
            order,
            day ?? Today,
            10
        );
    }

    // Themes: 1 Action {1, 3}, 2 Drama {1, 2}, 3 Empty {4: no video},
    // 4 Hidden {5: other library}.
    private Task SeedThemesAsync() =>
        SeedTitlesAsync(context =>
        {
            context.AnimeThemes.AddRange(
                new AnimeTheme { Id = 1, Name = "Action" },
                new AnimeTheme { Id = 2, Name = "Drama" },
                new AnimeTheme { Id = 3, Name = "Empty" },
                new AnimeTheme { Id = 4, Name = "Hidden" }
            );
            context.AnimeThemeTv.AddRange(
                new AnimeThemeTv { AnimeThemeId = 1, TvId = 1 },
                new AnimeThemeTv { AnimeThemeId = 1, TvId = 3 },
                new AnimeThemeTv { AnimeThemeId = 2, TvId = 1 },
                new AnimeThemeTv { AnimeThemeId = 2, TvId = 2 },
                new AnimeThemeTv { AnimeThemeId = 3, TvId = 4 },
                new AnimeThemeTv { AnimeThemeId = 4, TvId = 5 }
            );
        });

    [Fact]
    public async Task Themes_Newest_UsesTheNewestPlayableTitle_AndLeavesOutUnplayableAndHidden()
    {
        await SeedThemesAsync();

        // Drama holds the March show, Action only the February one. The empty
        // theme (no video) and the hidden theme (other library) do not appear.
        (await TopThemeIdsAsync(AnimeCarouselOrder.Newest))
            .Should()
            .Equal(2, 1);
    }

    [Fact]
    public async Task Themes_MostTitles_BreaksATieByName()
    {
        await SeedThemesAsync();

        (await TopThemeIdsAsync(AnimeCarouselOrder.MostTitles)).Should().Equal(1, 2);
    }

    [Fact]
    public async Task Themes_MostWatched_CountsOnlyThisUsersWatches()
    {
        await SeedThemesAsync();
        await WatchAsync(UserId, 2);
        // The other user watched two Action titles: if that counted, Action would lead.
        await WatchAsync(OtherUserId, 1, 3);

        (await TopThemeIdsAsync(AnimeCarouselOrder.MostWatched)).Should().Equal(2, 1);
    }

    [Fact]
    public async Task Themes_RandomDaily_IsTheSameTwiceOnOneDay()
    {
        await SeedThemesAsync();

        List<int> first = await TopThemeIdsAsync(AnimeCarouselOrder.RandomDaily);
        List<int> second = await TopThemeIdsAsync(AnimeCarouselOrder.RandomDaily);

        second.Should().Equal(first);
        first.Should().BeEquivalentTo([1, 2]);
    }

    [Fact]
    public async Task Themes_ByIds_ReturnsTheCardsInTheGivenOrder_WithPosters()
    {
        await SeedThemesAsync();

        await using MediaContext context = new(_options);
        List<AnimeThemeWithCountsDto> cards = await new AnimeThemeRepository(
            context
        ).GetThemesWithCountsByIdsAsync(UserId, "en", [2, 1, 4]);

        // Theme 4 is not in a library the user can see, so it is not returned.
        cards.Select(card => card.Id).Should().Equal(2, 1);
        cards[0].Name.Should().Be("Drama");
        cards[0].TvShowsWithVideo.Should().Be(2);
        cards[1].ItemPosters.Select(poster => poster.Src).Should().Equal("/1.jpg", "/3.jpg");
    }

    [Fact]
    public async Task Demographics_MostTitles_LeavesOutAGroupWithNoPlayableTitle()
    {
        await SeedTitlesAsync(context =>
        {
            context.AnimeDemographics.AddRange(
                new AnimeDemographic { Id = 1, Name = "Shonen" },
                new AnimeDemographic { Id = 2, Name = "Seinen" },
                new AnimeDemographic { Id = 3, Name = "Josei" }
            );
            context.AnimeDemographicTv.AddRange(
                new AnimeDemographicTv { AnimeDemographicId = 1, TvId = 1 },
                new AnimeDemographicTv { AnimeDemographicId = 2, TvId = 1 },
                new AnimeDemographicTv { AnimeDemographicId = 2, TvId = 2 },
                new AnimeDemographicTv { AnimeDemographicId = 3, TvId = 4 }
            );
        });

        await using MediaContext context = new(_options);
        AnimeDemographicRepository repository = new(context);

        (
            await repository.GetTopDemographicIdsAsync(
                UserId,
                AnimeCarouselOrder.MostTitles,
                Today,
                10
            )
        )
            .Should()
            .Equal(2, 1);
        (await repository.GetDemographicsWithCountsByIdsAsync(UserId, "en", [2, 1]))
            .Select(card => card.Id)
            .Should()
            .Equal(2, 1);
    }

    [Fact]
    public async Task Seasons_Newest_OrdersByYearThenQuarter_AndByIdsKeepsThatOrder()
    {
        await SeedTitlesAsync(context =>
        {
            context.AnimeSeasons.AddRange(
                new AnimeSeason
                {
                    Id = 1,
                    Year = 2023,
                    Quarter = "FALL",
                },
                new AnimeSeason
                {
                    Id = 2,
                    Year = 2024,
                    Quarter = "WINTER",
                },
                new AnimeSeason
                {
                    Id = 3,
                    Year = 2024,
                    Quarter = "SUMMER",
                },
                new AnimeSeason
                {
                    Id = 4,
                    Year = 2030,
                    Quarter = "FALL",
                }
            );
            context.AnimeSeasonTv.AddRange(
                new AnimeSeasonTv { AnimeSeasonId = 1, TvId = 1 },
                new AnimeSeasonTv { AnimeSeasonId = 2, TvId = 2 },
                new AnimeSeasonTv { AnimeSeasonId = 3, TvId = 3 },
                new AnimeSeasonTv { AnimeSeasonId = 4, TvId = 4 }
            );
        });

        await using MediaContext context = new(_options);
        AnimeSeasonRepository repository = new(context);

        List<int> ids = await repository.GetTopSeasonIdsAsync(
            UserId,
            AnimeCarouselOrder.Newest,
            Today,
            10
        );

        // Season 4 is the newest, but its only show has no video file.
        ids.Should().Equal(3, 2, 1);
        (await repository.GetSeasonsWithCountsByIdsAsync(UserId, ids))
            .Select(card => card.Id)
            .Should()
            .Equal(3, 2, 1);
    }
}
