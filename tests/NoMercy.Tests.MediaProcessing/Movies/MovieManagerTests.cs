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

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Database.Models.Common;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Movies;
using NoMercy.MediaProcessing.Shows;
using NoMercy.NmSystem.Information;
using NoMercy.Providers.TMDB.Client;
using NoMercy.Providers.TMDB.Models.Movies;
using NoMercy.Providers.TMDB.Models.Networks;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Factory;
using NoMercy.Tests.Common;

namespace NoMercy.Tests.MediaProcessing.Movies;

public class MovieManagerTests
{
    private readonly Mock<IMovieRepository> _movieRepositoryMock;
    private readonly Mock<ITmdbMovieClient> _movieClientMock;
    private readonly MovieManager _movieManager;
    private readonly TmdbMovieAppends _movieAppends;
    private readonly Library _library;
    private readonly int _movieId;

    public MovieManagerTests()
    {
        // TODO not using the app files and api info.
        AppFiles.CreateAppFolders().Wait();

        Mock<JobDispatcher> jobDispatcherMock = new();
        MovieResponseMocks mockDataProvider = new();

        _movieRepositoryMock = new();
        _movieClientMock = new();
        _movieClientMock
            .Setup(client => client.CompanyDetails(It.IsAny<int>(), It.IsAny<bool?>()))
            .ReturnsAsync(new TmdbTmdbNetworkDetails { Id = 1, Name = "Test Studio" });

        IStorageDriver storageDriver = new LocalStorageDriver();
        IStorageFactory storageFactory = new StorageFactory(
            storageDriver,
            NullLogger<StorageFactory>.Instance
        );
        _movieManager = new(
            _movieRepositoryMock.Object,
            jobDispatcherMock.Object,
            storageFactory,
            NullLogger<MovieManager>.Instance,
            Mock.Of<IAnimeEnrichmentService>(),
            movieClientFactory: _ => _movieClientMock.Object
        );
        _movieAppends = mockDataProvider.MockMovieAppendsResponse()!;
        _library = new() { Id = new(), Title = "Test Library" };
        _movieId = 1771;
    }

    [Fact]
    public async Task AddMovieAsync_ShouldAddMovie()
    {
        // Arrange
        _movieClientMock.Setup(client => client.WithAllAppends(false)).ReturnsAsync(_movieAppends);

        Movie capturedMovie = null!;

        _movieRepositoryMock
            .Setup(repo => repo.Add(It.IsAny<Movie>(), It.IsAny<bool>()))
            .Callback<Movie, bool>((movie, _) => capturedMovie = movie);

        // Act
        await _movieManager.Add(_movieId, _library);

        // Assert
        _movieRepositoryMock.Verify(
            repo => repo.Add(It.IsAny<Movie>(), It.IsAny<bool>()),
            Times.Once
        );
        _movieRepositoryMock.Verify(
            repo => repo.LinkToLibrary(_library, It.IsAny<Movie>()),
            Times.Once
        );
        Assert.NotNull(capturedMovie);
        Assert.Equal(_movieId, capturedMovie.Id);
        Assert.Equal(_movieAppends.Title, capturedMovie.Title);
    }

    // Closes the gap the other tests here leave open: they only assert
    // Add() was called with It.IsAny<bool>(), which would still pass if
    // MovieManager wired the flag backwards. _library has no
    // FolderLibraries, so ResolveFolder finds nothing - Add must be called
    // with folderDateIsReal literally false, not just "some bool".
    [Fact]
    public async Task AddMovieAsync_NoFolderConfigured_CallsAddWithFolderDateIsRealFalse()
    {
        _movieClientMock.Setup(client => client.WithAllAppends(false)).ReturnsAsync(_movieAppends);

        await _movieManager.Add(_movieId, _library);

        _movieRepositoryMock.Verify(repo => repo.Add(It.IsAny<Movie>(), false), Times.Once);
    }

    [Fact]
    public async Task UpdateMovieAsync_ShouldRefreshMovieViaUpsert()
    {
        _movieClientMock.Setup(client => client.WithAllAppends(false)).ReturnsAsync(_movieAppends);

        Movie capturedMovie = null!;
        _movieRepositoryMock
            .Setup(repo => repo.Add(It.IsAny<Movie>(), It.IsAny<bool>()))
            .Callback<Movie, bool>((movie, _) => capturedMovie = movie);

        await _movieManager.Update(_movieId, _library);

        _movieRepositoryMock.Verify(
            repo => repo.Add(It.IsAny<Movie>(), It.IsAny<bool>()),
            Times.Once
        );
        Assert.NotNull(capturedMovie);
        Assert.Equal(_movieId, capturedMovie.Id);
    }

    [Fact]
    public async Task RemoveMovieAsync_ShouldRemoveViaRepository()
    {
        _movieRepositoryMock.Setup(repo => repo.Remove(_movieId)).Returns(Task.CompletedTask);

        await _movieManager.Remove(_movieId, _library);

        _movieRepositoryMock.Verify(repo => repo.Remove(_movieId), Times.Once);
    }

    [Fact]
    public async Task StoreAlternativeTitles_ShouldStoreTitles()
    {
        await _movieManager.StoreAlternativeTitles(_movieAppends);

        _movieRepositoryMock.Verify(
            m => m.StoreAlternativeTitles(It.IsAny<IEnumerable<AlternativeTitle>>()),
            Times.Once
        );
    }

    [Fact]
    public async Task StoreTranslations_ShouldStoreTranslations()
    {
        await _movieManager.StoreTranslations(_movieAppends);

        _movieRepositoryMock.Verify(
            m => m.StoreTranslations(It.IsAny<IEnumerable<Translation>>()),
            Times.Once
        );
    }

    [Fact]
    public async Task StoreContentRatings_ShouldStoreRatings()
    {
        await _movieManager.StoreContentRatings(_movieAppends);

        _movieRepositoryMock.Verify(
            m => m.StoreContentRatings(It.IsAny<IEnumerable<CertificationMovie>>()),
            Times.Once
        );
    }

    [Fact]
    public async Task StoreSimilar_ShouldStoreSimilarMovies()
    {
        await _movieManager.StoreSimilar(_movieAppends);

        _movieRepositoryMock.Verify(
            m => m.StoreSimilar(It.IsAny<IEnumerable<Similar>>()),
            Times.Once
        );
    }

    [Fact]
    public async Task StoreRecommendations_ShouldStoreRecommendations()
    {
        await _movieManager.StoreRecommendations(_movieAppends);

        _movieRepositoryMock.Verify(
            m => m.StoreRecommendations(It.IsAny<IEnumerable<Recommendation>>()),
            Times.Once
        );
    }

    [Fact]
    public async Task StoreVideos_ShouldStoreVideos()
    {
        await _movieManager.StoreVideos(_movieAppends);

        _movieRepositoryMock.Verify(m => m.StoreVideos(It.IsAny<IEnumerable<Media>>()), Times.Once);
    }

    [Fact]
    public async Task StoreImages_ShouldStoreImages()
    {
        await _movieManager.StoreImages(_movieAppends);

        _movieRepositoryMock.Verify(
            m => m.StoreImages(It.IsAny<IEnumerable<Image>>()),
            Times.Exactly(3)
        );
    }

    [Fact]
    public async Task StoreKeywords_ShouldStoreKeywords()
    {
        await _movieManager.StoreKeywords(_movieAppends);

        _movieRepositoryMock.Verify(
            m => m.StoreKeywords(It.IsAny<IEnumerable<Keyword>>()),
            Times.Once
        );
        _movieRepositoryMock.Verify(
            m => m.LinkKeywordsToMovie(It.IsAny<IEnumerable<KeywordMovie>>()),
            Times.Once
        );
    }

    [Fact]
    public async Task StoreGenres_ShouldStoreGenres()
    {
        await _movieManager.StoreGenres(_movieAppends);

        _movieRepositoryMock.Verify(
            m => m.StoreGenres(It.IsAny<IEnumerable<GenreMovie>>()),
            Times.Once
        );
    }

    [Fact]
    public async Task StoreWatchProviders_ShouldStoreWatchProviders()
    {
        await _movieManager.StoreWatchProviders(_movieAppends);

        _movieRepositoryMock.Verify(
            m => m.StoreWatchProviders(It.IsAny<List<WatchProvider>>()),
            Times.Once
        );
        _movieRepositoryMock.Verify(
            m => m.StoreWatchProviderMedias(It.IsAny<List<WatchProviderMedia>>()),
            Times.Once
        );
    }

    [Fact]
    public async Task StoreCompanies_ShouldStoreCompanies()
    {
        await _movieManager.StoreCompanies(_movieAppends);

        _movieRepositoryMock.Verify(m => m.StoreCompanies(It.IsAny<List<Company>>()), Times.Once);
        _movieRepositoryMock.Verify(
            m => m.StoreCompanyMovies(It.IsAny<List<CompanyMovie>>()),
            Times.Once
        );
    }

    // Regression for Q3: EnrichMovieAsync used to run inside the same
    // Task.WhenAll as StoreGenres/StoreTranslations/StoreContentRatings, all
    // sharing one scoped MovieRepository.context — concurrent SaveChanges on
    // that context throws "A second operation was started on this context."
    // This proves EnrichMovieAsync is awaited only after StoreGenres has
    // already completed, mirroring ShowManager.AddShowAsync's sequencing.
    [Fact]
    public async Task AddMovieAsync_AwaitsEnrichMovieAsync_AfterStoreGenresCompletes()
    {
        Mock<IAnimeEnrichmentService> animeEnrichmentServiceMock = new();
        bool storeGenresCompleted = false;
        bool enrichSawStoreGenresCompleted = false;

        _movieRepositoryMock
            .Setup(repo => repo.StoreGenres(It.IsAny<IEnumerable<GenreMovie>>()))
            .Returns(async () =>
            {
                await Task.Delay(20);
                storeGenresCompleted = true;
            });

        animeEnrichmentServiceMock
            .Setup(s =>
                s.EnrichMovieAsync(
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<string[]?>(),
                    It.IsAny<bool?>()
                )
            )
            .Returns(() =>
            {
                enrichSawStoreGenresCompleted = storeGenresCompleted;
                return Task.CompletedTask;
            });

        IStorageDriver storageDriver = new LocalStorageDriver();
        IStorageFactory storageFactory = new StorageFactory(
            storageDriver,
            NullLogger<StorageFactory>.Instance
        );
        MovieManager movieManager = new(
            _movieRepositoryMock.Object,
            new Mock<JobDispatcher>().Object,
            storageFactory,
            NullLogger<MovieManager>.Instance,
            animeEnrichmentServiceMock.Object,
            movieClientFactory: _ => _movieClientMock.Object
        );

        _movieClientMock.Setup(client => client.WithAllAppends(false)).ReturnsAsync(_movieAppends);

        await movieManager.Add(_movieId, _library);

        animeEnrichmentServiceMock.Verify(
            s =>
                s.EnrichMovieAsync(
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<string[]?>(),
                    It.IsAny<bool?>()
                ),
            Times.Once
        );
        Assert.True(enrichSawStoreGenresCompleted);
    }
}
