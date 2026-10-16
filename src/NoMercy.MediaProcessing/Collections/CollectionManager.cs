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

using Microsoft.Extensions.Logging;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.MediaProcessing.Common;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.MediaProcessing.Movies;
using NoMercy.NmSystem;
using NoMercy.NmSystem.Extensions;
using NoMercy.Providers.TMDB.Client;
using NoMercy.Providers.TMDB.Models.Collections;
using NoMercy.Providers.TMDB.Models.Movies;

namespace NoMercy.MediaProcessing.Collections;

public class CollectionManager(
    ICollectionRepository collectionRepository,
    MovieManager movieManager,
    JobDispatcher jobDispatcher,
    ILogger<CollectionManager> logger,
    Func<int, ITmdbMovieClient>? movieClientFactory = null
) : BaseManager, ICollectionManager
{
    private readonly Func<int, ITmdbMovieClient> _movieClientFactory =
        movieClientFactory ?? (movieId => new TmdbMovieClient(movieId));

    public async Task<TmdbCollectionAppends?> Add(int id, Library library)
    {
        TmdbCollectionClient collectionClient = new(id);
        TmdbCollectionAppends? collectionAppends = await collectionClient.WithAllAppends();

        if (collectionAppends is null)
            return null;

        Collection collection = new()
        {
            Id = collectionAppends.Id,
            Title = collectionAppends.Name,
            TitleSort = collectionAppends.Name.TitleSort(
                collectionAppends.Parts.MinBy(movie => movie.ReleaseDate)?.ReleaseDate
            ),
            Backdrop = collectionAppends.BackdropPath,
            Poster = collectionAppends.PosterPath,
            Overview = collectionAppends.Overview,
            Parts = collectionAppends.Parts.Length,

            LibraryId = library.Id,
        };

        await collectionRepository.Store(collection);

        logger.LogDebug("Collection: {Title}: Added to Database", collection.Title);

        await StoreTranslations(collectionAppends);

        jobDispatcher.DispatchColorPaletteJob("collection", collection.Id.ToString());
        jobDispatcher.DispatchJob<CollectionExtrasJob, TmdbCollectionAppends>(collectionAppends);

        logger.LogDebug(
            "Collection: {Name}: Added to Library {Title}",
            [collectionAppends.Name, library.Title]
        );

        return collectionAppends;
    }

    public Task UpdateCollectionAsync(int id, Library library)
    {
        // Re-importing refreshes all metadata via idempotent upserts.
        return Add(id, library);
    }

    public async Task RemoveCollectionAsync(int id, Library library)
    {
        await collectionRepository.Remove(id);
        logger.LogDebug("Collection: {Id}: Removed from Database", id);
    }

    private async Task StoreTranslations(TmdbCollectionAppends collection)
    {
        IEnumerable<Translation> translations = collection.Translations.Translations.Select(
            translation => new Translation
            {
                Iso31661 = translation.Iso31661,
                Iso6391 = translation.Iso6391,
                Name = translation.Name == "" ? null : translation.Name,
                Title = translation.Data.Title == "" ? null : translation.Data.Title,
                Overview = translation.Data.Overview == "" ? null : translation.Data.Overview,
                EnglishName = translation.EnglishName,
                Homepage = translation.Data.Homepage?.ToString(),
                CollectionId = collection.Id,
            }
        );

        await collectionRepository.StoreTranslations(translations);

        logger.LogDebug("Collection: {Name}: Translations stored", collection.Name);
    }

    internal async Task StoreImages(TmdbCollectionAppends collection)
    {
        IEnumerable<Image> posters = collection
            .Images.Posters.Select(image => new Image
            {
                AspectRatio = image.AspectRatio,
                FilePath = image.FilePath,
                Height = image.Height,
                Iso6391 = image.Iso6391,
                VoteAverage = image.VoteAverage,
                VoteCount = image.VoteCount,
                Width = image.Width,
                CollectionId = collection.Id,
                Type = "poster",
                Site = "https://image.tmdb.org/t/p/",
            })
            .ToArray();

        await collectionRepository.StoreImages(posters);
        logger.LogDebug("Movie: {Name}: Posters stored", collection.Name);

        IEnumerable<Image> backdrops = collection
            .Images.Backdrops.Select(image => new Image
            {
                AspectRatio = image.AspectRatio,
                FilePath = image.FilePath,
                Height = image.Height,
                Iso6391 = image.Iso6391,
                VoteAverage = image.VoteAverage,
                VoteCount = image.VoteCount,
                Width = image.Width,
                CollectionId = collection.Id,
                Type = "backdrop",
                Site = "https://image.tmdb.org/t/p/",
            })
            .ToArray();

        await collectionRepository.StoreImages(backdrops);
        logger.LogDebug("Collection: {Name}: backdrops stored", collection.Name);

        IEnumerable<Image> logos = collection
            .Images.Logos.Select(image => new Image
            {
                AspectRatio = image.AspectRatio,
                FilePath = image.FilePath,
                Height = image.Height,
                Iso6391 = image.Iso6391,
                VoteAverage = image.VoteAverage,
                VoteCount = image.VoteCount,
                Width = image.Width,
                CollectionId = collection.Id,
                Type = "logo",
                Site = "https://image.tmdb.org/t/p/",
            })
            .ToArray();

        await collectionRepository.StoreImages(logos);
        logger.LogDebug("Collection: {Name}: Logos stored", collection.Name);
    }

    public async Task AddCollectionMovies(TmdbCollectionAppends collectionAppends, Library library)
    {
        List<TmdbMovieAppends> movies = await FetchPartsAsync(collectionAppends);

        foreach (TmdbMovieAppends movie in movies)
            await movieManager.Add(movie.Id, library);

        await collectionRepository.LinkToMovies(collectionAppends);

        logger.LogDebug("Collection: {Name}: Movies added", collectionAppends.Name);
    }

    /// <summary>
    /// Fetches every part of the collection in parallel and returns the ones
    /// TMDB knows, in the order of the collection.
    /// <para>
    /// Each part writes only its own slot of the array, so the parallel
    /// fetches share nothing. A plain List here lost parts, or threw, when two
    /// fetches finished at once (#366).
    /// </para>
    /// </summary>
    internal async Task<List<TmdbMovieAppends>> FetchPartsAsync(
        TmdbCollectionAppends collectionAppends
    )
    {
        TmdbMovieAppends?[] fetched = new TmdbMovieAppends?[collectionAppends.Parts.Length];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, fetched.Length),
            SystemParallelism.Options,
            async (index, _) =>
            {
                ITmdbMovieClient movieClient = _movieClientFactory(
                    collectionAppends.Parts[index].Id
                );
                try
                {
                    fetched[index] = await movieClient.WithAllAppends(false);
                }
                finally
                {
                    (movieClient as IDisposable)?.Dispose();
                }
            }
        );

        return [.. fetched.OfType<TmdbMovieAppends>()];
    }
}
