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

// ---------------------------------------------------------------------------------------------------------------------
// Imports
// ---------------------------------------------------------------------------------------------------------------------

using Microsoft.Extensions.Logging;
using NoMercy.Database;
using NoMercy.Events;
using NoMercy.Events.Library;
using NoMercy.MediaProcessing.Movies;
using NoMercy.MediaProcessing.People;
using NoMercy.MediaProcessing.Shows;
using NoMercy.Providers.AniList;
using NoMercy.Providers.Jikan;
using NoMercy.Providers.TMDB.Models.Movies;
using NoMercy.Storage;

namespace NoMercy.MediaProcessing.Jobs.MediaJobs;

// ---------------------------------------------------------------------------------------------------------------------
// Code
// ---------------------------------------------------------------------------------------------------------------------
[Serializable]
public class MovieExtrasJob : AbstractMediaExraDataJob<TmdbMovieAppends>
{
    public MovieExtrasJob() { }

    public MovieExtrasJob(
        IStorageFactory storageFactory,
        IStorageDriver storageDriver,
        ILoggerFactory loggerFactory
    )
        : base(storageFactory, storageDriver, loggerFactory) { }

    public override string QueueName => "extras";
    public override int Priority => 1;

    public override async Task Handle()
    {
        await using MediaContext context = new();
        JobDispatcher jobDispatcher = new();

        MovieRepository movieRepository = new(context);
        AnimeEnrichmentService animeEnrichmentService = new(
            new MediaTypeClassifier(new AniListMetadataProvider(), new JikanMetadataProvider()),
            new AniListMetadataProvider(),
            new JikanMetadataProvider(),
            new ShowRepository(context),
            movieRepository
        );
        MovieManager movieManager = new(
            movieRepository,
            jobDispatcher,
            StorageFactory,
            LoggerFactory.CreateLogger<MovieManager>(),
            animeEnrichmentService,
            PluginMetadata
        );

        PersonRepository personRepository = new(
            context,
            LoggerFactory.CreateLogger<PersonRepository>()
        );
        PersonManager personManager = new(
            personRepository,
            jobDispatcher,
            LoggerFactory.CreateLogger<PersonManager>()
        );

        await personManager.Store(Storage).WithTimeout(nameof(PersonManager.Store));

        await movieManager.StoreImages(Storage).WithTimeout(nameof(MovieManager.StoreImages));
        await movieManager.StoreSimilar(Storage).WithTimeout(nameof(MovieManager.StoreSimilar));
        await movieManager
            .StoreRecommendations(Storage)
            .WithTimeout(nameof(MovieManager.StoreRecommendations));
        await movieManager
            .StoreAlternativeTitles(Storage)
            .WithTimeout(nameof(MovieManager.StoreAlternativeTitles));
        await movieManager
            .StoreWatchProviders(Storage)
            .WithTimeout(nameof(MovieManager.StoreWatchProviders));
        await movieManager.StoreVideos(Storage).WithTimeout(nameof(MovieManager.StoreVideos));
        await movieManager.StoreCompanies(Storage).WithTimeout(nameof(MovieManager.StoreCompanies));
        await movieManager.StoreKeywords(Storage).WithTimeout(nameof(MovieManager.StoreKeywords));

        if (EventBusProvider.IsConfigured)
            await EventBusProvider.Current.PublishAsync(
                new LibraryRefreshedEvent { QueryKey = ["base", "info", Storage.Id.ToString()] }
            );
    }
}
