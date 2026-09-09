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

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NoMercy.Database;
using NoMercy.Database.Models.Common;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.MediaProcessing.Common;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.MediaProcessing.Shows;
using NoMercy.NmSystem;
using NoMercy.NmSystem.Extensions;
using NoMercy.Plugins.Hooks;
using NoMercy.Providers.TMDB.Client;
using NoMercy.Providers.TMDB.Models.Movies;
using NoMercy.Providers.TMDB.Models.Networks;
using NoMercy.Providers.TMDB.Models.Shared;
using NoMercy.Storage;
using PluginMediaMetadata = NoMercy.Plugins.Abstractions.MediaMetadata;
using PluginMediaType = NoMercy.Plugins.Abstractions.MediaType;

namespace NoMercy.MediaProcessing.Movies;

public class MovieManager(
    IMovieRepository movieRepository,
    JobDispatcher jobDispatcher,
    IStorageFactory storageFactory,
    ILogger<MovieManager> logger,
    IAnimeEnrichmentService animeEnrichmentService,
    IPluginMetadataResolver? pluginMetadata = null,
    Func<int, ITmdbMovieClient>? movieClientFactory = null
) : BaseManager, IMovieManager
{
    private readonly Func<int, ITmdbMovieClient> _movieClientFactory =
        movieClientFactory ?? (movieId => new TmdbMovieClient(movieId));

    public async Task<TmdbMovieAppends?> Add(int id, Library library)
    {
        logger.LogInformation("Movie: {Id}: Adding to Library {Title}", [id, library.Title]);

        ITmdbMovieClient movieClient = _movieClientFactory(id);
        try
        {
            TmdbMovieAppends? movieAppends = await MetadataRetry.FetchAsync(
                () => movieClient.WithAllAppends(false),
                $"TMDB movie {id}"
            );
            return await AddFromAppends(id, library, movieAppends);
        }
        finally
        {
            (movieClient as IDisposable)?.Dispose();
        }
    }

    private async Task<TmdbMovieAppends?> AddFromAppends(
        int id,
        Library library,
        TmdbMovieAppends? movieAppends
    )
    {
        if (movieAppends == null)
            return null;

        string? title = movieAppends.Title;
        if (string.IsNullOrEmpty(title))
        {
            logger.LogWarning("Movie: {Id}: Title is null or empty, skipping.", id);
            return null;
        }

        // What plugins know that TMDB did not answer. Asked after the provider
        // and merged with ?? below, so a plugin fills a gap and never overwrites
        // what TMDB already said — which would make a title's metadata depend on
        // which plugin happened to load first.
        PluginMediaMetadata? fromPlugins = await (
            pluginMetadata ?? NullPluginMetadataResolver.Instance
        ).ResolveAsync(title, PluginMediaType.Movie);

        string baseUrl = BaseUrl(title, movieAppends.ReleaseDate);

        (bool folderDateIsReal, DateTime folderCreatedAt) = ResolveFolder(library, baseUrl);

        Movie movie = new()
        {
            LibraryId = library.Id,
            Folder = baseUrl,

            Id = movieAppends.Id,
            Title = movieAppends.Title,
            TitleSort = movieAppends.Title.TitleSort(movieAppends.ReleaseDate),
            Duration = movieAppends.Runtime,
            Adult = movieAppends.Adult,
            Backdrop = movieAppends.BackdropPath,
            Budget = movieAppends.Budget,
            Homepage = movieAppends.Homepage?.ToString(),
            ImdbId = movieAppends.ImdbId,
            OriginalTitle = movieAppends.OriginalTitle,
            OriginalLanguage = movieAppends.OriginalLanguage,
            OriginCountry =
                movieAppends.OriginCountry.Length > 0 ? movieAppends.OriginCountry[0] : null,
            Overview = movieAppends.Overview ?? fromPlugins?.Overview,
            Popularity = movieAppends.Popularity,
            Poster = movieAppends.PosterPath ?? fromPlugins?.PosterUrl,
            ReleaseDate = movieAppends.ReleaseDate,
            Revenue = movieAppends.Revenue,
            Runtime = movieAppends.Runtime,
            Status = movieAppends.Status,
            Tagline = movieAppends.Tagline,
            Trailer = movieAppends.Video?.ToString(),
            Video = movieAppends.Video?.ToString(),
            VoteAverage = movieAppends.VoteAverage,
            VoteCount = movieAppends.VoteCount,

            CreatedAt = folderCreatedAt,
        };

        await movieRepository.Add(movie, folderDateIsReal);
        logger.LogDebug("Movie: {Title}: Added to Database", movie.Title);

        await movieRepository.LinkToLibrary(library, movie);
        logger.LogDebug("Movie: {Title}: Linked to Library {Title2}", [movie.Title, library.Title]);

        await Task.WhenAll([
            StoreTranslations(movieAppends),
            StoreGenres(movieAppends),
            StoreContentRatings(movieAppends),
        ]);

        // Sequential and awaited on purpose, mirroring ShowManager.AddShowAsync:
        // EnrichMovieAsync ends up doing EF work on the same scoped
        // MovieRepository.context as the calls above, so it cannot run inside
        // the same Task.WhenAll without a "second operation on this context"
        // exception.
        await animeEnrichmentService.EnrichMovieAsync(
            movie.Id,
            movieAppends.Title,
            movieAppends.ReleaseDate.ParseYear(),
            movieAppends.OriginCountry
        );

        logger.LogInformation(
            "Movie: {Title}: Added to Library {Title2}",
            [movieAppends.Title, library.Title]
        );

        jobDispatcher.DispatchColorPaletteJob("movie", movie.Id.ToString());
        jobDispatcher.DispatchJob<MovieExtrasJob, TmdbMovieAppends>(movieAppends);

        return movieAppends;
    }

    // Structural ground truth: does this movie's folder physically exist
    // under ANY of the given library's configured folders? Returns the real
    // folder creation date when found, or DateTime.UtcNow (a sentinel the
    // caller must not mistake for a real date) when it does not - mirrors
    // ShowManager.ResolveFolder.
    private (bool exists, DateTime createdAt) ResolveFolder(Library library, string baseUrl)
    {
        DateTime folderCreatedAt = DateTime.UtcNow;

        foreach (FolderLibrary folderLibrary in library.FolderLibraries ?? [])
        {
            if (storageFactory == null)
                continue;

            IStorage folderStorage = storageFactory.For(
                folderLibrary.Folder.Id,
                folderLibrary.Folder.DriverId,
                string.Empty
            );
            string folderRoot = FolderRootPath(folderStorage, folderLibrary.Folder.Path);
            string folderName = folderStorage.CombinePath(folderRoot, baseUrl.Replace("/", ""));

            if (!folderStorage.Exists(folderName))
            {
                string? match = FileNameSanitizer.FindMatchingDirectory(
                    folderStorage.Driver,
                    folderRoot,
                    baseUrl.Replace("/", "")
                );
                if (match != null)
                    folderName = match;
            }

            if (!folderStorage.Exists(folderName))
                continue;

            folderCreatedAt = folderStorage.Driver.GetCreationTimeUtc(folderName);
            return (true, folderCreatedAt);
        }

        return (false, folderCreatedAt);
    }

    public Task Update(int id, Library library)
    {
        // Re-importing refreshes all metadata. Every write in Add is an
        // idempotent upsert, so re-running it updates the existing records
        // in place rather than creating duplicates.
        return Add(id, library);
    }

    public async Task Remove(int id, Library library)
    {
        logger.LogInformation("Movie: {Id}: Removing from Library {Title}", [id, library.Title]);
        await movieRepository.Remove(id);
        logger.LogDebug("Movie: {Id}: Removed from Database", id);
    }

    public async Task StoreAlternativeTitles(TmdbMovieAppends movie)
    {
        IEnumerable<AlternativeTitle> alternativeTitles = (
            movie.AlternativeTitles?.Results ?? []
        ).Select(tmdbMovieAlternativeTitles => new AlternativeTitle
        {
            Iso31661 = tmdbMovieAlternativeTitles.Iso31661,
            Title = tmdbMovieAlternativeTitles.Title,
            MovieId = movie.Id,
        });

        await movieRepository.StoreAlternativeTitles(alternativeTitles);

        logger.LogDebug("Movie: {Title}: AlternativeTitles stored", movie.Title);
    }

    public async Task StoreTranslations(TmdbMovieAppends movie)
    {
        IEnumerable<Translation> translations = (movie.Translations?.Translations ?? []).Select(
            translation => new Translation
            {
                Iso31661 = translation.Iso31661,
                Iso6391 = translation.Iso6391,
                Name = translation.Name == "" ? null : translation.Name,
                Title = translation.Data.Title == "" ? null : translation.Data.Title,
                Overview = translation.Data.Overview == "" ? null : translation.Data.Overview,
                EnglishName = translation.EnglishName,
                Homepage = translation.Data.Homepage?.ToString(),
                MovieId = movie.Id,
            }
        );

        await movieRepository.StoreTranslations(translations);

        logger.LogDebug("Movie: {Title}: Translations stored", movie.Title);
    }

    public async Task StoreContentRatings(TmdbMovieAppends movie)
    {
        List<CertificationCriteria> certificationCriteria = (movie.ReleaseDates?.Results ?? [])
            .Select(r => new CertificationCriteria
            {
                Iso31661 = r.Iso31661,
                Certification = r.ReleaseDates.FirstOrDefault()?.Certification ?? string.Empty,
            })
            .ToList();

        IEnumerable<CertificationMovie> certificationMovies =
            movieRepository.GetCertificationMovies(movie, certificationCriteria);

        await movieRepository.StoreContentRatings(certificationMovies);

        logger.LogDebug("Movie: {Title}: Content Ratings stored", movie.Title);
    }

    public async Task StoreSimilar(TmdbMovieAppends movie)
    {
        IEnumerable<Similar> similar = (movie.Similar?.Results ?? [])
            .Select(tmdbMovie => new Similar
            {
                Backdrop = tmdbMovie.BackdropPath,
                Overview = tmdbMovie.Overview,
                Poster = tmdbMovie.PosterPath,
                Title = tmdbMovie.Title,
                TitleSort = tmdbMovie.Title?.TitleSort(),
                MediaId = tmdbMovie.Id,
                MovieFromId = movie.Id,
            })
            .ToArray();

        await movieRepository.StoreSimilar(similar);

        logger.LogDebug("Movie: {Title}: Similar stored", movie.Title);

        await using MediaContext db = new();
        List<int> similarIds = await db
            .Similar.AsNoTracking()
            .Where(s =>
                s.MovieFromId == movie.Id && (s._colorPalette == null || s._colorPalette == "")
            )
            .Select(s => s.Id)
            .ToListAsync();

        foreach (int id in similarIds)
            jobDispatcher.DispatchColorPaletteJob("similar", id.ToString());
    }

    public async Task StoreRecommendations(TmdbMovieAppends movie)
    {
        IEnumerable<Recommendation> recommendations = (movie.Recommendations?.Results ?? [])
            .Select(tmdbMovie => new Recommendation
            {
                Backdrop = tmdbMovie.BackdropPath,
                Overview = tmdbMovie.Overview,
                Poster = tmdbMovie.PosterPath,
                Title = tmdbMovie.Title,
                TitleSort = tmdbMovie.Title?.TitleSort(),
                MediaId = tmdbMovie.Id,
                MovieFromId = movie.Id,
            })
            .ToArray();

        await movieRepository.StoreRecommendations(recommendations);

        logger.LogDebug("Movie: {Title}: Recommendations stored", movie.Title);

        await using MediaContext db = new();
        List<int> recommendationIds = await db
            .Recommendations.AsNoTracking()
            .Where(r =>
                r.MovieFromId == movie.Id && (r._colorPalette == null || r._colorPalette == "")
            )
            .Select(r => r.Id)
            .ToListAsync();

        foreach (int id in recommendationIds)
            jobDispatcher.DispatchColorPaletteJob("recommendation", id.ToString());
    }

    public async Task StoreVideos(TmdbMovieAppends movie)
    {
        IEnumerable<Media> videos = (movie.Videos?.Results ?? []).Select(media => new Media
        {
            Id = Ulid.NewUlid(),
            Iso6391 = media.Iso6391,
            Name = media.Name,
            Site = media.Site,
            Size = media.Size,
            Src = media.Key,
            Type = media.Type,
            MovieId = movie.Id,
        });

        await movieRepository.StoreVideos(videos);
        logger.LogDebug("Movie: {Title}: Videos stored", movie.Title);
    }

    public async Task StoreImages(TmdbMovieAppends movie)
    {
        IEnumerable<Image> posters = (movie.Images?.Posters ?? [])
            .Select(image => new Image
            {
                AspectRatio = image.AspectRatio,
                FilePath = image.FilePath,
                Height = image.Height,
                Iso6391 = image.Iso6391,
                VoteAverage = image.VoteAverage,
                VoteCount = image.VoteCount,
                Width = image.Width,
                MovieId = movie.Id,
                Type = "poster",
                Site = "https://image.tmdb.org/t/p/",
            })
            .ToArray();

        await movieRepository.StoreImages(posters);
        logger.LogDebug("Movie: {Title}: Posters stored", movie.Title);

        IEnumerable<Image> backdrops = (movie.Images?.Backdrops ?? [])
            .Select(image => new Image
            {
                AspectRatio = image.AspectRatio,
                FilePath = image.FilePath,
                Height = image.Height,
                Iso6391 = image.Iso6391,
                VoteAverage = image.VoteAverage,
                VoteCount = image.VoteCount,
                Width = image.Width,
                MovieId = movie.Id,
                Type = "backdrop",
                Site = "https://image.tmdb.org/t/p/",
            })
            .ToArray();

        await movieRepository.StoreImages(backdrops);
        logger.LogDebug("Movie: {Title}: backdrops stored", movie.Title);

        IEnumerable<Image> logos = (movie.Images?.Logos ?? [])
            .Select(image => new Image
            {
                AspectRatio = image.AspectRatio,
                FilePath = image.FilePath,
                Height = image.Height,
                Iso6391 = image.Iso6391,
                VoteAverage = image.VoteAverage,
                VoteCount = image.VoteCount,
                Width = image.Width,
                MovieId = movie.Id,
                Type = "logo",
                Site = "https://image.tmdb.org/t/p/",
            })
            .ToArray();

        await movieRepository.StoreImages(logos);
        logger.LogDebug("Movie: {Title}: Logos stored", movie.Title);

        await using MediaContext db = new();
        List<int> imageIds = await db
            .Images.AsNoTracking()
            .Where(i => i.MovieId == movie.Id && (i._colorPalette == null || i._colorPalette == ""))
            .Select(i => i.Id)
            .ToListAsync();

        foreach (int id in imageIds)
            jobDispatcher.DispatchColorPaletteJob("image", id.ToString());
    }

    public async Task StoreKeywords(TmdbMovieAppends movie)
    {
        IEnumerable<Keyword> keywords = (movie.Keywords?.Results ?? []).Select(
            keyword => new Keyword { Id = keyword.Id, Name = keyword.Name }
        );

        await movieRepository.StoreKeywords(keywords);
        logger.LogDebug("Movie: {Title}: Keywords stored", movie.Title);

        IEnumerable<KeywordMovie> keywordMovies = (movie.Keywords?.Results ?? []).Select(
            keyword => new KeywordMovie { KeywordId = keyword.Id, MovieId = movie.Id }
        );

        await movieRepository.LinkKeywordsToMovie(keywordMovies);
        logger.LogDebug("Movie: {Title}: Keywords linked to Movie", movie.Title);
    }

    public async Task StoreGenres(TmdbMovieAppends movie)
    {
        IEnumerable<GenreMovie> genreMovies = (movie.Genres ?? []).Select(genre => new GenreMovie
        {
            GenreId = genre.Id,
            MovieId = movie.Id,
        });

        await movieRepository.StoreGenres(genreMovies);
        logger.LogDebug("Movie: {Title}: Genres stored", movie.Title);
    }

    public async Task StoreWatchProviders(TmdbMovieAppends movie)
    {
        List<WatchProvider> watchProviders = [];
        List<WatchProviderMedia> watchProviderMedias = [];

        foreach (
            (
                string countryCode,
                string providerType,
                TmdbPaymentDetails provider,
                string? link
            ) in TmdbWatchProviders.ExtractProviders(
                movie.WatchProviders?.TmdbWatchProviderResults ?? new()
            )
        )
        {
            if (watchProviders.All(wp => wp.Id != provider.ProviderId))
            {
                watchProviders.Add(
                    new()
                    {
                        Id = provider.ProviderId,
                        Name = provider.ProviderName,
                        Logo = provider.LogoPath,
                        DisplayPriority = provider.DisplayPriority,
                    }
                );
            }

            watchProviderMedias.Add(
                new()
                {
                    WatchProviderId = provider.ProviderId,
                    MovieId = movie.Id,
                    CountryCode = countryCode,
                    ProviderType = providerType,
                    Link = link,
                }
            );
        }

        if (watchProviders.Count != 0)
            await movieRepository.StoreWatchProviders(watchProviders);

        if (watchProviderMedias.Count != 0)
            await movieRepository.StoreWatchProviderMedias(watchProviderMedias);

        logger.LogDebug("Show {Title}: WatchProviders stored", movie.Title);
    }

    public async Task StoreCompanies(TmdbMovieAppends movie)
    {
        if (movie.ProductionCompanies == null || movie.ProductionCompanies.Length == 0)
        {
            logger.LogDebug("Movie: {Title}: No production companies found", movie.Title);
            return;
        }

        ITmdbMovieClient movieClient = _movieClientFactory(movie.Id);

        ConcurrentDictionary<int, Company> companiesDict = new();

        try
        {
            await Parallel.ForEachAsync(
                movie.ProductionCompanies,
                SystemParallelism.Options,
                async (productionCompany, _) =>
                {
                    TmdbTmdbNetworkDetails? nw = await movieClient.CompanyDetails(
                        productionCompany.Id
                    );
                    if (nw == null)
                        return;

                    companiesDict.TryAdd(
                        nw.Id,
                        new()
                        {
                            Id = nw.Id,
                            Name = nw.Name,
                            Logo = nw.LogoPath,
                            OriginCountry = nw.OriginCountry,
                            Headquarters = nw.Headquarters,
                            Homepage = nw.Homepage,
                        }
                    );
                }
            );

            List<Company> companies = companiesDict.Values.ToList();

            List<CompanyMovie> companyMovies = companies
                .Select(company => new CompanyMovie { CompanyId = company.Id, MovieId = movie.Id })
                .ToList();

            if (companies.Count != 0)
                await movieRepository.StoreCompanies(companies);

            if (companyMovies.Count != 0)
                await movieRepository.StoreCompanyMovies(companyMovies);

            logger.LogDebug("Movie: {Title}: Companies stored", movie.Title);
        }
        finally
        {
            (movieClient as IDisposable)?.Dispose();
        }
    }
}
