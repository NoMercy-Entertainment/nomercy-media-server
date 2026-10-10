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

/// <summary>
/// CHAR-06: Query output tests for every repository method via ToQueryString() or SQL interceptor.
/// Verifies that EF Core generates correct SQL for each repository query method.
/// Methods returning IQueryable use ToQueryString() directly.
/// Methods that materialize results use SqlCaptureInterceptor to capture executed SQL.
/// Simple CRUD operations (Add/Update/Delete/Like/Upsert) are excluded.
/// Compiled queries (EF.CompileAsyncQuery) are tested via interceptor execution.
/// </summary>
[Trait("Category", "Characterization")]
public class QueryOutputTests : IDisposable
{
    private readonly MediaContext _context;
    private readonly SqlCaptureInterceptor _interceptor;
    private readonly IDbContextFactory<MediaContext> _homeFactory;
    private readonly SqliteConnection _homeFactoryConnection;

    public QueryOutputTests()
    {
        (_homeFactory, _interceptor, _homeFactoryConnection) =
            TestMediaContextFactory.CreateSeededFactoryWithInterceptor();
        _context = _homeFactory.CreateDbContext();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        _homeFactoryConnection.Dispose();
    }

    #region MovieRepository

    [Fact]
    public async Task MovieRepository_GetMovieAsync_GeneratesExpectedSql()
    {
        MovieRepository repository = new(_homeFactory, NullLogger<MovieRepository>.Instance);
        _interceptor.Clear();

        await repository.GetMovieAsync(SeedConstants.UserId, 129, "en", "US");

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Movies", sql);
        Assert.Contains("WHERE", sql);
        Assert.Contains("LibraryUser", sql);
    }

    [Fact]
    public async Task MovieRepository_GetMovieAvailableAsync_GeneratesExpectedSql()
    {
        MovieRepository repository = new(_homeFactory, NullLogger<MovieRepository>.Instance);
        _interceptor.Clear();

        await repository.GetMovieAvailableAsync(SeedConstants.UserId, 129);

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Movies", sql);
        Assert.Contains("LibraryUser", sql);
        Assert.Contains("VideoFiles", sql);
    }

    [Fact]
    public async Task MovieRepository_GetMoviePlaylistAsync_GeneratesExpectedSql()
    {
        MovieRepository repository = new(_homeFactory, NullLogger<MovieRepository>.Instance);
        _interceptor.Clear();

        await repository.GetMoviePlaylistAsync(SeedConstants.UserId, 129, "en", "US");

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Movies", sql);
        Assert.Contains("VideoFiles", sql);
        Assert.Contains("CertificationMovie", sql);
    }

    [Fact]
    public async Task MovieRepository_DeleteMovieAsync_GeneratesDeleteSql()
    {
        MovieRepository repository = new(_homeFactory, NullLogger<MovieRepository>.Instance);
        _interceptor.Clear();

        await repository.DeleteAsync(999);

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("DELETE", sql);
        Assert.Contains("Movies", sql);
    }

    #endregion

    #region TvShowRepository

    [Fact]
    public async Task TvShowRepository_GetTvAvailableAsync_GeneratesExpectedSql()
    {
        TvShowRepository repository = new(_homeFactory, new StubMediaTypeClassifier());
        _interceptor.Clear();

        await repository.GetTvAvailableAsync(SeedConstants.UserId, 1399);

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Tvs", sql);
        Assert.Contains("LibraryUser", sql);
        Assert.Contains("Episodes", sql);
        Assert.Contains("VideoFiles", sql);
    }

    [Fact]
    public async Task TvShowRepository_GetTvPlaylistAsync_GeneratesExpectedSql()
    {
        TvShowRepository repository = new(_homeFactory, new StubMediaTypeClassifier());
        _interceptor.Clear();

        await repository.GetPlaylistAsync(SeedConstants.UserId, 1399, "en", "US");

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Tvs", sql);
        Assert.Contains("Seasons", sql);
        Assert.Contains("Episodes", sql);
        Assert.Contains("VideoFiles", sql);
    }

    [Fact]
    public async Task TvShowRepository_DeleteTvAsync_GeneratesDeleteSql()
    {
        TvShowRepository repository = new(_homeFactory, new StubMediaTypeClassifier());
        _interceptor.Clear();

        await repository.DeleteAsync(999);

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("DELETE", sql);
        Assert.Contains("Tvs", sql);
    }

    [Fact]
    public async Task TvShowRepository_GetMissingLibraryShows_GeneratesExpectedSql()
    {
        TvShowRepository repository = new(_homeFactory, new StubMediaTypeClassifier());
        _interceptor.Clear();

        await repository.GetMissingLibraryShows(SeedConstants.UserId, 1399, "en");

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Tvs", sql);
        Assert.Contains("Episodes", sql);
        Assert.Contains("LibraryUser", sql);
    }

    #endregion

    #region GenreRepository

    [Fact]
    public async Task GenreRepository_GetGenres_GeneratesExpectedSql()
    {
        GenreRepository repository = new(_context);
        _interceptor.Clear();

        await repository.GetGenres(SeedConstants.UserId, "en", 10, 0);

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Genres", sql);
        Assert.Contains("GenreMovie", sql);
        Assert.Contains("GenreTv", sql);
        Assert.Contains("LibraryUser", sql);
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("LIMIT", sql);
    }

    [Fact]
    public async Task GenreRepository_GetGenreAsync_GeneratesExpectedSql()
    {
        GenreRepository repository = new(_context);
        _interceptor.Clear();

        await repository.GetGenreAsync(SeedConstants.UserId, 18, "en", "US", 10, 0);

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Genres", sql);
        Assert.Contains("GenreMovie", sql);
        Assert.Contains("LibraryUser", sql);
        Assert.Contains("VideoFiles", sql);
    }

    [Fact]
    public async Task GenreRepository_GetGenresWithCountsAsync_GeneratesProjectionSql()
    {
        GenreRepository repository = new(_context);
        _interceptor.Clear();

        await repository.GetGenresWithCountsAsync(SeedConstants.UserId, "en", 10, 0);

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Genres", sql);
        Assert.Contains("GenreMovie", sql);
        Assert.Contains("GenreTv", sql);
        Assert.Contains("ORDER BY", sql);
    }

    [Fact]
    public async Task GenreRepository_GetMusicGenresAsync_GeneratesExpectedSql()
    {
        GenreRepository repository = new(_context);
        _interceptor.Clear();

        await repository.GetMusicGenresAsync(SeedConstants.UserId);

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("MusicGenres", sql);
    }

    [Fact]
    public async Task GenreRepository_GetPaginatedMusicGenresAsync_GeneratesExpectedSql()
    {
        GenreRepository repository = new(_context);
        _interceptor.Clear();

        await repository.GetPaginatedMusicGenresAsync(SeedConstants.UserId, "R", 10, 0);

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("MusicGenres", sql);
        Assert.Contains("LIMIT", sql);
    }

    [Fact]
    public async Task GenreRepository_GetMusicGenreAsync_GeneratesExpectedSql()
    {
        GenreRepository repository = new(_context);
        _interceptor.Clear();

        await repository.GetMusicGenreAsync(SeedConstants.UserId, Guid.NewGuid());

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("MusicGenres", sql);
        Assert.Contains("MusicGenreTrack", sql);
    }

    #endregion

    #region HomeRepository

    [Fact]
    public async Task HomeRepository_GetHomeTvs_GeneratesExpectedSql()
    {
        HomeRepository repository = new(_context, _homeFactory);
        _interceptor.Clear();

        await repository.GetHomeTvs([1399], "en", "US");

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Tvs", sql);
        Assert.Contains("Episodes", sql);
        Assert.Contains("VideoFiles", sql);
    }

    [Fact]
    public async Task HomeRepository_GetHomeMovies_GeneratesExpectedSql()
    {
        HomeRepository repository = new(_context, _homeFactory);
        _interceptor.Clear();

        await repository.GetHomeMovies([129, 680], "en", "US");

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Movies", sql);
        Assert.Contains("VideoFiles", sql);
    }

    [Fact]
    public async Task HomeRepository_GetContinueWatchingAsync_GeneratesExpectedSql()
    {
        HomeRepository repository = new(_context, _homeFactory);
        _interceptor.Clear();

        await repository.GetContinueWatchingAsync(SeedConstants.UserId, "en", "US");

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("UserData", sql);
    }

    [Fact]
    public async Task HomeRepository_GetScreensaverImagesAsync_GeneratesExpectedSql()
    {
        HomeRepository repository = new(_context, _homeFactory);
        _interceptor.Clear();

        await repository.GetScreensaverImagesAsync(SeedConstants.UserId);

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Images", sql);
        Assert.Contains("LibraryUser", sql);
    }

    [Fact]
    public async Task HomeRepository_GetLibrariesAsync_GeneratesExpectedSql()
    {
        HomeRepository repository = new(_context, _homeFactory);
        _interceptor.Clear();

        await repository.GetLibrariesAsync(SeedConstants.UserId);

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Libraries", sql);
        Assert.Contains("LibraryUser", sql);
    }

    [Fact]
    public async Task HomeRepository_GetMovieCountAsync_GeneratesCountSql()
    {
        HomeRepository repository = new(_context, _homeFactory);
        _interceptor.Clear();

        await repository.GetMovieCountAsync(SeedConstants.UserId);

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Movies", sql);
        Assert.Contains("COUNT", sql);
    }

    [Fact]
    public async Task HomeRepository_GetTvCountAsync_GeneratesCountSql()
    {
        HomeRepository repository = new(_context, _homeFactory);
        _interceptor.Clear();

        await repository.GetTvCountAsync(SeedConstants.UserId);

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Tvs", sql);
        Assert.Contains("COUNT", sql);
    }

    [Fact]
    public async Task HomeRepository_GetAnimeCountAsync_GeneratesCountSql()
    {
        HomeRepository repository = new(_context, _homeFactory);
        _interceptor.Clear();

        await repository.GetAnimeCountAsync(SeedConstants.UserId);

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Tvs", sql);
        Assert.Contains("COUNT", sql);
    }

    [Fact]
    public async Task HomeRepository_GetHomeGenresAsync_GeneratesExpectedSql()
    {
        HomeRepository repository = new(_context, _homeFactory);
        _interceptor.Clear();

        await repository.GetHomeGenresAsync(SeedConstants.UserId, "en", 10);

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Genres", sql);
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("LIMIT", sql);
    }

    #endregion

    #region LibraryRepository

    [Fact]
    public async Task LibraryRepository_GetLibraries_GeneratesExpectedSql()
    {
        LibraryRepository repository = new(_homeFactory);
        _interceptor.Clear();

        await repository.GetLibraries(SeedConstants.UserId);

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Libraries", sql);
        Assert.Contains("LibraryUser", sql);
        Assert.Contains("ORDER BY", sql);
    }

    [Fact]
    public async Task LibraryRepository_GetLibraryByIdAsync_WithPagination_GeneratesExpectedSql()
    {
        LibraryRepository repository = new(_homeFactory);
        _interceptor.Clear();

        await repository.GetLibraryByIdAsync(
            SeedConstants.MovieLibraryId,
            SeedConstants.UserId,
            "en",
            "US",
            10,
            0
        );

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Libraries", sql);
        Assert.Contains("LibraryUser", sql);
        Assert.Contains("LibraryMovie", sql);
    }

    [Fact]
    public async Task LibraryRepository_GetLibraryMovieCardsAsync_GeneratesProjectionSql()
    {
        LibraryRepository repository = new(_homeFactory);
        _interceptor.Clear();

        await repository.GetLibraryMovieCardsAsync(
            SeedConstants.UserId,
            SeedConstants.MovieLibraryId,
            "US",
            10,
            0
        );

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Movies", sql);
        Assert.Contains("LibraryUser", sql);
        Assert.Contains("VideoFiles", sql);
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("LIMIT", sql);
    }

    [Fact]
    public async Task LibraryRepository_GetLibraryTvCardsAsync_GeneratesProjectionSql()
    {
        LibraryRepository repository = new(_homeFactory);
        _interceptor.Clear();

        await repository.GetLibraryTvCardsAsync(
            SeedConstants.UserId,
            SeedConstants.TvLibraryId,
            "US",
            10,
            0
        );

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Tvs", sql);
        Assert.Contains("LibraryUser", sql);
        Assert.Contains("Episodes", sql);
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("LIMIT", sql);
    }

    [Fact]
    public async Task LibraryRepository_GetPaginatedLibraryMovies_GeneratesExpectedSql()
    {
        LibraryRepository repository = new(_homeFactory);
        _interceptor.Clear();

        await repository.GetPaginatedLibraryMovies(
            SeedConstants.UserId,
            SeedConstants.MovieLibraryId,
            "F",
            "en",
            "US",
            10,
            0
        );

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Movies", sql);
        Assert.Contains("VideoFiles", sql);
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("LIMIT", sql);
    }

    [Fact]
    public async Task LibraryRepository_GetPaginatedLibraryShows_GeneratesExpectedSql()
    {
        LibraryRepository repository = new(_homeFactory);
        _interceptor.Clear();

        await repository.GetPaginatedLibraryShows(
            SeedConstants.UserId,
            SeedConstants.TvLibraryId,
            "B",
            "en",
            "US",
            10,
            0
        );

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Tvs", sql);
        Assert.Contains("Episodes", sql);
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("LIMIT", sql);
    }

    [Fact]
    public async Task LibraryRepository_GetLibraryByIdAsync_Simple_GeneratesExpectedSql()
    {
        LibraryRepository repository = new(_homeFactory);
        _interceptor.Clear();

        await repository.GetLibraryByIdAsync(SeedConstants.MovieLibraryId);

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Libraries", sql);
        Assert.Contains("FolderLibrary", sql);
    }

    [Fact]
    public async Task LibraryRepository_GetAllLibrariesAsync_GeneratesExpectedSql()
    {
        LibraryRepository repository = new(_homeFactory);
        _interceptor.Clear();

        await repository.GetAllLibrariesAsync();

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Libraries", sql);
        Assert.Contains("FolderLibrary", sql);
    }

    [Fact]
    public async Task LibraryRepository_GetFoldersAsync_GeneratesProjectionSql()
    {
        LibraryRepository repository = new(_homeFactory);
        _interceptor.Clear();

        await repository.GetFoldersAsync();

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Folders", sql);
    }

    [Fact]
    public async Task LibraryRepository_GetRandomTvShow_GeneratesExpectedSql()
    {
        LibraryRepository repository = new(_homeFactory);
        _interceptor.Clear();

        await repository.GetRandomTvShow(SeedConstants.UserId, "en");

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Tvs", sql);
        Assert.Contains("LibraryUser", sql);
    }

    [Fact]
    public async Task LibraryRepository_GetRandomMovie_GeneratesExpectedSql()
    {
        LibraryRepository repository = new(_homeFactory);
        _interceptor.Clear();

        await repository.GetRandomMovie(SeedConstants.UserId, "en");

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Movies", sql);
        Assert.Contains("LibraryUser", sql);
    }

    #endregion

    #region CollectionRepository

    [Fact]
    public async Task CollectionRepository_GetCollectionsAsync_GeneratesExpectedSql()
    {
        CollectionRepository repository = new(_homeFactory);
        _interceptor.Clear();

        await repository.GetCollectionsAsync(SeedConstants.UserId, "en", 10, 0);

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Collections", sql);
        Assert.Contains("LibraryUser", sql);
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("LIMIT", sql);
    }

    [Fact]
    public async Task CollectionRepository_GetCollectionsListAsync_GeneratesProjectionSql()
    {
        Collection collection = new()
        {
            Id = 9001,
            Title = "Posters",
            LibraryId = SeedConstants.MovieLibraryId,
        };
        _context.Collections.Add(collection);
        _context.CollectionMovie.AddRange(
            new CollectionMovie(collection.Id, 129),
            new CollectionMovie(collection.Id, 680)
        );
        _context.Movies.Single(movie => movie.Id == 129).Poster = "/first";
        _context.Movies.Single(movie => movie.Id == 680).Poster = "/second";
        await _context.SaveChangesAsync();

        CollectionRepository repository = new(_homeFactory);
        _interceptor.Clear();

        List<CollectionListDto> result = await repository.GetCollectionsListAsync(
            SeedConstants.UserId,
            "en",
            "US",
            10,
            0
        );

        Assert.NotEmpty(_interceptor.CapturedSql);
        Assert.Equal(["/second", "/first"], result.Single().ItemPosters);
        Assert.Single(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Collections", sql);
        Assert.Contains("CollectionMovie", sql);
        Assert.Contains("LibraryUser", sql);
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("LIMIT", sql);
    }

    [Fact]
    public async Task CollectionRepository_GetCollectionAsync_GeneratesExpectedSql()
    {
        CollectionRepository repository = new(_homeFactory);
        _interceptor.Clear();

        await repository.GetCollectionAsync(SeedConstants.UserId, 1, "en", "US");

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Collections", sql);
        Assert.Contains("LibraryUser", sql);
    }

    [Fact]
    public async Task CollectionRepository_GetCollectionItems_GeneratesExpectedSql()
    {
        CollectionRepository repository = new(_homeFactory);
        _interceptor.Clear();

        await repository.GetCollectionItems(SeedConstants.UserId, "en", "US", 10);

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Collections", sql);
        Assert.Contains("LibraryUser", sql);
        Assert.Contains("CollectionMovie", sql);
        Assert.Contains("ORDER BY", sql);
    }

    [Fact]
    public async Task CollectionRepository_GetAvailableCollectionAsync_GeneratesExpectedSql()
    {
        CollectionRepository repository = new(_homeFactory);
        _interceptor.Clear();

        await repository.GetAvailableCollectionAsync(SeedConstants.UserId, 1);

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Collections", sql);
        Assert.Contains("LibraryUser", sql);
        Assert.Contains("CollectionMovie", sql);
        Assert.Contains("VideoFiles", sql);
    }

    [Fact]
    public async Task CollectionRepository_GetCollectionPlaylistAsync_GeneratesExpectedSql()
    {
        CollectionRepository repository = new(_homeFactory);
        _interceptor.Clear();

        await repository.GetCollectionPlaylistAsync(SeedConstants.UserId, 1, "en", "US");

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Collections", sql);
        Assert.Contains("LibraryUser", sql);
        // CollectionMovie and VideoFiles may appear in split queries
        Assert.True(
            _interceptor.CapturedSql.Count >= 1,
            "Expected at least one query for collection playlist"
        );
    }

    #endregion

    #region SpecialRepository

    [Fact]
    public async Task SpecialRepository_GetSpecialsAsync_GeneratesExpectedSql()
    {
        SpecialRepository repository = new(_context, _homeFactory);
        _interceptor.Clear();

        await repository.GetSpecialsAsync(SeedConstants.UserId, "en", 10, 0);

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Specials", sql);
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("LIMIT", sql);
    }

    [Fact]
    public void SpecialRepository_GetSpecialAsync_GeneratesExpectedSql()
    {
        SpecialRepository repository = new(_context, _homeFactory);
        _interceptor.Clear();

        // GetSpecialAsync uses Task.FromResult wrapping a synchronous query
        repository.GetSpecialAsync(SeedConstants.UserId, Ulid.NewUlid());

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Specials", sql);
    }

    [Fact]
    public async Task SpecialRepository_GetSpecialItems_GeneratesExpectedSql()
    {
        SpecialRepository repository = new(_context, _homeFactory);
        _interceptor.Clear();

        await repository.GetSpecialItems(SeedConstants.UserId, "en", "US", 10);

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Specials", sql);
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("LIMIT", sql);
    }

    [Fact]
    public async Task SpecialRepository_GetSpecialPlaylistAsync_GeneratesExpectedSql()
    {
        SpecialRepository repository = new(_context, _homeFactory);
        _interceptor.Clear();

        await repository.GetSpecialPlaylistAsync(SeedConstants.UserId, Ulid.NewUlid(), "en", "US");

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Specials", sql);
    }

    #endregion

    #region DeviceRepository

    [Fact]
    public async Task DeviceRepository_GetDevices_GeneratesExpectedSql()
    {
        DeviceRepository repository = new(_context);
        _interceptor.Clear();

        // Execute the query to capture all split queries
        await repository.GetDevices();

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Devices", sql);
    }

    #endregion

    #region FolderRepository

    [Fact]
    public async Task FolderRepository_GetFolderByIdAsync_GeneratesExpectedSql()
    {
        FolderRepository repository = new(_context);
        _interceptor.Clear();

        await repository.GetFolderByIdAsync(SeedConstants.MovieFolderId);

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Folders", sql);
        Assert.Contains("FolderLibrary", sql);
    }

    [Fact]
    public async Task FolderRepository_GetFolderByPathAsync_GeneratesExpectedSql()
    {
        FolderRepository repository = new(_context);
        _interceptor.Clear();

        await repository.GetFolderByPathAsync("/media/movies");

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Folders", sql);
    }

    [Fact]
    public async Task FolderRepository_GetFoldersByLibraryIdAsync_GeneratesExpectedSql()
    {
        FolderRepository repository = new(_context);
        _interceptor.Clear();

        await repository.GetFoldersByLibraryIdAsync(SeedConstants.MovieLibraryId);

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("FolderLibrary", sql);
        Assert.Contains("Folders", sql);
    }

    [Fact]
    public async Task FolderRepository_GetFolderById_GeneratesExpectedSql()
    {
        FolderRepository repository = new(_context);
        _interceptor.Clear();

        await repository.GetFolderById(SeedConstants.MovieFolderId);

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Folders", sql);
    }

    [Fact]
    public async Task FolderRepository_GetFolderByPath_GeneratesExpectedSql()
    {
        FolderRepository repository = new(_context);
        _interceptor.Clear();

        await repository.GetFolderByPath("/media/movies");

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Folders", sql);
    }

    #endregion

    #region LanguageRepository

    [Fact]
    public async Task LanguageRepository_GetLanguagesAsync_GeneratesExpectedSql()
    {
        LanguageRepository repository = new(_context);
        _interceptor.Clear();

        await repository.GetLanguagesAsync();

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Languages", sql);
    }

    [Fact]
    public async Task LanguageRepository_GetLanguagesAsync_WithFilter_GeneratesExpectedSql()
    {
        LanguageRepository repository = new(_context);
        _interceptor.Clear();

        await repository.GetLanguagesAsync(["en", "fr"]);

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("LanguageLibrary", sql);
    }

    #endregion

    #region MovieRepository - Compiled Query

    [Fact]
    public async Task MovieRepository_GetMovieDetailAsync_CompiledQuery_GeneratesExpectedSql()
    {
        MovieRepository repository = new(_homeFactory, NullLogger<MovieRepository>.Instance);
        _interceptor.Clear();

        await repository.GetMovieDetailAsync(SeedConstants.UserId, 129, "en", "US");

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Movies", sql);
        Assert.Contains("LibraryUser", sql);
        Assert.Contains("Casts", sql);
    }

    #endregion

    #region TvShowRepository - Split Detail Query

    [Fact]
    public async Task TvShowRepository_GetTvAsync_SplitQuery_GeneratesExpectedSql()
    {
        TvShowRepository repository = new(_homeFactory, new StubMediaTypeClassifier());
        _interceptor.Clear();

        await repository.GetTvAsync(SeedConstants.UserId, 1399, "en", "US");

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("Tvs", sql);
        Assert.Contains("LibraryUser", sql);
        Assert.Contains("Seasons", sql);
        Assert.Contains("Episodes", sql);
        Assert.Contains("Casts", sql);
    }

    #endregion

    #region MED-02: Existence checks use EXISTS instead of COUNT

    [Fact]
    public async Task HomeRepository_GetHomeTvs_UsesExistsNotCount()
    {
        HomeRepository repository = new(_context, _homeFactory);
        _interceptor.Clear();

        await repository.GetHomeTvs([1399], "en", "US");

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("EXISTS", sql);
        Assert.DoesNotContain("COUNT(*) > 0", sql);
    }

    [Fact]
    public async Task HomeRepository_GetHomeMovies_UsesExistsNotCount()
    {
        HomeRepository repository = new(_context, _homeFactory);
        _interceptor.Clear();

        await repository.GetHomeMovies([129, 680], "en", "US");

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("EXISTS", sql);
        Assert.DoesNotContain("COUNT(*) > 0", sql);
    }

    [Fact]
    public async Task HomeRepository_GetHomeGenres_UsesExistsForVideoFileCheck()
    {
        HomeRepository repository = new(_context, _homeFactory);
        _interceptor.Clear();

        await repository.GetHomeGenresAsync(SeedConstants.UserId, "en", 10);

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("EXISTS", sql);
    }

    [Fact]
    public async Task GenreRepository_GetMusicGenresAsync_UsesExistsNotCount()
    {
        GenreRepository repository = new(_context);
        _interceptor.Clear();

        await repository.GetMusicGenresAsync(SeedConstants.UserId);

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("EXISTS", sql);
        Assert.DoesNotContain("COUNT(*) > 0", sql);
    }

    [Fact]
    public async Task TvShowRepository_GetMissingLibraryShows_UsesExistsForEmptyVideoFiles()
    {
        TvShowRepository repository = new(_homeFactory, new StubMediaTypeClassifier());
        _interceptor.Clear();

        await repository.GetMissingLibraryShows(SeedConstants.UserId, 1399, "en");

        Assert.NotEmpty(_interceptor.CapturedSql);
        string sql = string.Join(" ", _interceptor.CapturedSql);
        Assert.Contains("EXISTS", sql);
        Assert.DoesNotContain("COUNT(*) > 0", sql);
    }

    #endregion
}
