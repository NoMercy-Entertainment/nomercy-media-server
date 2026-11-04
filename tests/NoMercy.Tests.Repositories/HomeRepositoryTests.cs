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
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Users;
using NoMercy.Tests.Repositories.Infrastructure;

namespace NoMercy.Tests.Repositories;

[Trait("Category", "Characterization")]
public class HomeRepositoryTests : IDisposable
{
    private readonly MediaContext _context;
    private readonly HomeRepository _repository;
    private readonly IDbContextFactory<MediaContext> _factory;
    private readonly SqliteConnection _factoryConnection;

    public HomeRepositoryTests()
    {
        _context = TestMediaContextFactory.CreateSeededContext();
        (_factory, _factoryConnection) = TestMediaContextFactory.CreateFactory();
        _repository = new(_context, _factory);
    }

    [Fact]
    public async Task GetScreensaverImagesAsync_IncludesLogoWithoutPalette()
    {
        _context.Images.AddRange(
            new Image
            {
                MovieId = 129,
                Type = "backdrop",
                FilePath = "/backdrop.jpg",
                Height = 1080,
                ColorPalette = new() { Image = new() { Dominant = "#123456" } },
            },
            new Image
            {
                MovieId = 129,
                Type = "logo",
                Iso6391 = "en",
                FilePath = "/logo.png",
            }
        );
        await _context.SaveChangesAsync();

        HashSet<Image> images = await _repository.GetScreensaverImagesAsync(SeedConstants.UserId);

        Assert.Contains(images, image => image.Type == "backdrop");
        Assert.Contains(images, image => image.Type == "logo" && image.ColorPalette is null);
    }

    [Fact]
    public async Task GetHomeMovies_ReturnsMoviesById()
    {
        List<HomeMovieCardDto> movies = await _repository.GetHomeMovies([129, 680], "en", "US");

        Assert.Equal(2, movies.Count);
        Assert.Contains(movies, m => m.Id == 129);
        Assert.Contains(movies, m => m.Id == 680);
    }

    [Fact]
    public async Task GetHomeMovies_ReturnsEmpty_WhenIdsNotFound()
    {
        List<HomeMovieCardDto> movies = await _repository.GetHomeMovies([999999], "en", "US");

        Assert.Empty(movies);
    }

    [Fact]
    public async Task GetHomeTvs_ReturnsTvShowsById()
    {
        List<HomeTvCardDto> shows = await _repository.GetHomeTvs([1399], "en", "US");

        Assert.Single(shows);
        Assert.Equal(1399, shows[0].Id);
    }

    [Fact]
    public async Task GetMovieCountAsync_ReturnsCorrectCount()
    {
        int count = await _repository.GetMovieCountAsync(SeedConstants.UserId);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task GetTvCountAsync_ReturnsCorrectCount()
    {
        int count = await _repository.GetTvCountAsync(SeedConstants.UserId);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task GetMovieCountAsync_ReturnsZero_WhenUserHasNoAccess()
    {
        int count = await _repository.GetMovieCountAsync(SeedConstants.OtherUserId);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task GetLibrariesAsync_ReturnsLibrariesForUser()
    {
        List<Library> libraries = await _repository.GetLibrariesAsync(SeedConstants.UserId);

        Assert.Equal(2, libraries.Count);
    }

    [Fact]
    public async Task GetLibrariesAsync_ReturnsEmpty_WhenUserHasNoAccess()
    {
        List<Library> libraries = await _repository.GetLibrariesAsync(SeedConstants.OtherUserId);

        Assert.Empty(libraries);
    }

    [Fact]
    public async Task GetHomeGenresAsync_ReturnsGenresForUser()
    {
        List<GenreHomeDto> genres = await _repository.GetHomeGenresAsync(
            SeedConstants.UserId,
            "en",
            10
        );

        Assert.Equal(2, genres.Count);
        Assert.Contains(genres, g => g.Name == "Action");
        Assert.Contains(genres, g => g.Name == "Drama");
    }

    [Fact]
    public async Task GetHomeGenresAsync_RespectsPageAndTake()
    {
        List<GenreHomeDto> genres = await _repository.GetHomeGenresAsync(
            SeedConstants.UserId,
            "en",
            1
        );

        Assert.Single(genres);
    }

    [Fact]
    public async Task GetContinueWatchingAsync_ReturnsDeduplicated()
    {
        // Seed has 3 UserData rows: 2 for movie 129 (duplicate), 1 for tv 1399
        // DistinctBy on { MovieId, CollectionId, TvId, SpecialId } should yield 2 unique entries
        HashSet<UserData> result = await _repository.GetContinueWatchingAsync(
            SeedConstants.UserId,
            "en",
            "US"
        );

        Assert.Equal(2, result.Count);
        Assert.Contains(result, ud => ud.MovieId == 129);
        Assert.Contains(result, ud => ud.TvId == 1399);
    }

    [Fact]
    public async Task GetContinueWatchingAsync_KeepsMostRecentPerGroup()
    {
        // The most recent entry for movie 129 has LastPlayedDate 2026-02-01
        HashSet<UserData> result = await _repository.GetContinueWatchingAsync(
            SeedConstants.UserId,
            "en",
            "US"
        );

        UserData? movieEntry = result.FirstOrDefault(ud => ud.MovieId == 129);
        Assert.NotNull(movieEntry);
        Assert.Equal("2026-02-01T10:00:00Z", movieEntry.LastPlayedDate);
    }

    [Fact]
    public async Task GetContinueWatchingAsync_IncludesVideoFile()
    {
        HashSet<UserData> result = await _repository.GetContinueWatchingAsync(
            SeedConstants.UserId,
            "en",
            "US"
        );

        Assert.All(result, ud => Assert.NotNull(ud.VideoFile));
    }

    [Fact]
    public async Task GetContinueWatchingAsync_IncludesMovieData()
    {
        HashSet<UserData> result = await _repository.GetContinueWatchingAsync(
            SeedConstants.UserId,
            "en",
            "US"
        );

        UserData? movieEntry = result.FirstOrDefault(ud => ud.MovieId == 129);
        Assert.NotNull(movieEntry);
        Assert.NotNull(movieEntry.Movie);
        Assert.NotEmpty(movieEntry.Movie.VideoFiles);
    }

    [Fact]
    public async Task GetContinueWatchingAsync_ReturnsEmpty_WhenNoUserData()
    {
        HashSet<UserData> result = await _repository.GetContinueWatchingAsync(
            SeedConstants.OtherUserId,
            "en",
            "US"
        );

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetContinueWatchingAsync_ExcludesRowsHiddenFromContinueWatching()
    {
        // Hide both movie 129 rows the way UserDataController.RemoveContinue does —
        // the rows must stay in the table, only disappear from the carousel query.
        List<UserData> movieRows = await _context
            .UserData.Where(ud => ud.MovieId == 129)
            .ToListAsync();
        foreach (UserData row in movieRows)
            row.RemovedFromContinueWatching = true;
        await _context.SaveChangesAsync();

        HashSet<UserData> result = await _repository.GetContinueWatchingAsync(
            SeedConstants.UserId,
            "en",
            "US"
        );

        Assert.Single(result);
        Assert.DoesNotContain(result, ud => ud.MovieId == 129);
        Assert.Contains(result, ud => ud.TvId == 1399);

        // The hidden rows must still exist for the recommendation engine.
        int survivingRows = await _context.UserData.CountAsync(ud => ud.MovieId == 129);
        Assert.Equal(2, survivingRows);
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        _factoryConnection.Dispose();
    }
}
