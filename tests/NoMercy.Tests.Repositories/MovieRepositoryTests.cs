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
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Movies;
using NoMercy.Tests.Repositories.Infrastructure;

namespace NoMercy.Tests.Repositories;

[Trait("Category", "Characterization")]
public class MovieRepositoryTests : IDisposable
{
    private readonly MediaContext _context;
    private readonly IDbContextFactory<MediaContext> _factory;
    private readonly SqliteConnection _connection;
    private readonly MovieRepository _repository;

    public MovieRepositoryTests()
    {
        (_factory, _connection) = TestMediaContextFactory.CreateSeededFactory();
        _context = _factory.CreateDbContext();
        _repository = new(_factory, NullLogger<MovieRepository>.Instance);
    }

    [Fact]
    public async Task GetMovieAsync_ReturnsMovie_WhenUserHasAccess()
    {
        Movie? movie = await _repository.GetMovieAsync(SeedConstants.UserId, 129, "en", "US");

        Assert.NotNull(movie);
        Assert.Equal(129, movie.Id);
        Assert.Equal("Spirited Away", movie.Title);
    }

    [Fact]
    public async Task GetMovieAsync_ReturnsNull_WhenUserHasNoAccess()
    {
        Movie? movie = await _repository.GetMovieAsync(SeedConstants.OtherUserId, 129, "en", "US");

        Assert.Null(movie);
    }

    [Fact]
    public async Task GetMovieAsync_ReturnsNull_WhenMovieDoesNotExist()
    {
        Movie? movie = await _repository.GetMovieAsync(SeedConstants.UserId, 999999, "en", "US");

        Assert.Null(movie);
    }

    [Fact]
    public async Task GetMovieAsync_IncludesVideoFiles()
    {
        Movie? movie = await _repository.GetMovieAsync(SeedConstants.UserId, 129, "en", "US");

        Assert.NotNull(movie);
        Assert.NotEmpty(movie.VideoFiles);
        Assert.Contains(movie.VideoFiles, vf => vf.Filename == "Spirited.Away.2001.1080p.mkv");
    }

    [Fact]
    public async Task GetMovieAvailableAsync_ReturnsTrue_WhenMovieHasVideoFiles()
    {
        bool available = await _repository.GetMovieAvailableAsync(SeedConstants.UserId, 129);

        Assert.True(available);
    }

    [Fact]
    public async Task GetMovieAvailableAsync_ReturnsFalse_WhenUserHasNoAccess()
    {
        bool available = await _repository.GetMovieAvailableAsync(SeedConstants.OtherUserId, 129);

        Assert.False(available);
    }

    [Fact]
    public async Task GetMoviePlaylistAsync_ReturnsMovieWithVideoFiles()
    {
        List<Movie> playlist = await _repository.GetMoviePlaylistAsync(
            SeedConstants.UserId,
            129,
            "en",
            "US"
        );

        Assert.NotEmpty(playlist);
        Assert.Equal(129, playlist[0].Id);
        Assert.NotEmpty(playlist[0].VideoFiles);
    }

    [Fact]
    public async Task DeleteMovieAsync_RemovesMovie()
    {
        await _repository.DeleteAsync(129);

        Movie? movie = await _repository.GetMovieAsync(SeedConstants.UserId, 129, "en", "US");

        Assert.Null(movie);
    }

    [Fact]
    public async Task LikeMovieAsync_AddsMovieUser_WhenLikeIsTrue()
    {
        bool result = await _repository.LikeMovieAsync(129, SeedConstants.UserId, true);

        Assert.True(result);

        MovieUser? movieUser = _context.MovieUser.FirstOrDefault(mu =>
            mu.MovieId == 129 && mu.UserId == SeedConstants.UserId
        );
        Assert.NotNull(movieUser);
    }

    [Fact]
    public async Task LikeMovieAsync_RemovesMovieUser_WhenLikeIsFalse()
    {
        await _repository.LikeMovieAsync(129, SeedConstants.UserId, true);
        bool result = await _repository.LikeMovieAsync(129, SeedConstants.UserId, false);

        Assert.True(result);

        MovieUser? movieUser = _context.MovieUser.FirstOrDefault(mu =>
            mu.MovieId == 129 && mu.UserId == SeedConstants.UserId
        );
        Assert.Null(movieUser);
    }

    [Fact]
    public async Task GetMovieDetailAsync_RecommendsOnlyOwnedPlayableNotAdultMovies()
    {
        Ulid closedLibraryId = Ulid.NewUlid();
        _context.Libraries.Add(
            new()
            {
                Id = closedLibraryId,
                Title = "Closed",
                Type = "movie",
            }
        );
        _context.Movies.Add(
            new()
            {
                Id = 900002,
                Title = "In a closed library",
                TitleSort = "in a closed library",
                LibraryId = closedLibraryId,
            }
        );
        _context.VideoFiles.Add(
            new()
            {
                Id = Ulid.NewUlid(),
                Filename = "closed.mkv",
                Folder = "/media/closed",
                HostFolder = "/media/closed",
                Languages = "en",
                Quality = "1080p",
                Share = "movies",
                MovieId = 900002,
            }
        );
        // 680 is owned with a file; 900001 is not on the server; 900002 is in a closed library.
        foreach (int relatedId in new[] { 680, 900001, 900002 })
            _context.Recommendations.Add(new() { MediaId = relatedId, MovieFromId = 129 });
        await _context.SaveChangesAsync();

        Movie? movie = await _repository.GetMovieDetailAsync(SeedConstants.UserId, 129, "en", "US");

        Assert.NotNull(movie);
        Assert.Equal([680], movie.RecommendationFrom.Select(related => related.MediaId));
        Assert.All(movie.RecommendationFrom, related => Assert.False(related.Adult));
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        _connection.Dispose();
    }
}
