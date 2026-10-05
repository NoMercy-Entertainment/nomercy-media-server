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

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Events;
using NoMercy.Events.Library;
using NoMercy.Events.Media;
using NoMercy.MediaProcessing.Common;
using NoMercy.MediaProcessing.Movies;
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
public class MovieImportJob : AbstractMediaJob
{
    public MovieImportJob() { }

    public MovieImportJob(
        IStorageFactory storageFactory,
        IStorageDriver storageDriver,
        ILoggerFactory loggerFactory
    )
        : base(storageFactory, storageDriver, loggerFactory) { }

    public override string QueueName => "import";
    public override int Priority => 5;

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

        Library? movieLibrary = await context
            .Libraries.Where(library => library.Id == LibraryId)
            .Include(library => library.FolderLibraries)
                .ThenInclude(folderLibrary => folderLibrary.Folder)
            .FirstOrDefaultAsync();

        if (movieLibrary is null)
        {
            Log.LogInformation(
                "MovieImportJob: library {LibraryId} not found, skipping movie {Id}",
                LibraryId,
                Id
            );
            return;
        }

        bool wasEmpty = !await context.LibraryMovie.AnyAsync(lm => lm.LibraryId == LibraryId);

        TmdbMovieAppends? movieAppends = await ImportAndPublishAsync(
            context,
            () => movieManager.Add(Id, movieLibrary),
            EventBusProvider.IsConfigured ? EventBusProvider.Current : null
        );
        if (movieAppends == null)
        {
            await ImportFailureRecorder.RecordAsync(
                context,
                "MovieImportJob",
                Id.ToString(),
                LibraryId,
                "TMDB movie metadata fetch returned no result after retries."
            );
            return;
        }

        if (movieAppends.BelongsToCollection != null)
            jobDispatcher.DispatchJob<CollectionImportJob>(
                movieAppends.BelongsToCollection.Id,
                LibraryId
            );

        jobDispatcher.DispatchJob<FileRescanJob>(Id, movieLibrary);

        Log.LogInformation(
            "Movie {Id} added to library, extra data will be added in the background",
            Id
        );

        if (EventBusProvider.IsConfigured)
        {
            await EventBusProvider.Current.PublishAsync(
                new LibraryRefreshedEvent { QueryKey = ["base", "info", Id.ToString()] }
            );

            if (wasEmpty)
                await EventBusProvider.Current.PublishAsync(
                    new LibraryRefreshedEvent { QueryKey = ["libraries"] }
                );
        }
    }

    internal async Task<TmdbMovieAppends?> ImportAndPublishAsync(
        MediaContext context,
        Func<Task<TmdbMovieAppends?>> add,
        IEventBus? eventBus
    )
    {
        // Membership lives in two stores (Movie.LibraryId and LibraryMovie),
        // as for shows; older rows may only have Movie.LibraryId.
        bool isNewToLibrary =
            !await context
                .Movies.IgnoreQueryFilters()
                .AsNoTracking()
                .AnyAsync(movie => movie.Id == Id && movie.LibraryId != default)
            && !await context
                .LibraryMovie.IgnoreQueryFilters()
                .AsNoTracking()
                .AnyAsync(link => link.MovieId == Id);
        TmdbMovieAppends? movieAppends = await add();
        if (isNewToLibrary && movieAppends is not null && eventBus is not null)
            await eventBus.PublishAsync(
                new MediaAddedEvent
                {
                    MediaId = Id,
                    MediaType = "movie",
                    Title = movieAppends.Title,
                    LibraryId = LibraryId,
                }
            );

        return movieAppends;
    }
}
