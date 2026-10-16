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
using NoMercy.MediaProcessing.Collections;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Movies;
using NoMercy.MediaProcessing.Shows;
using NoMercy.Providers.TMDB.Client;
using NoMercy.Providers.TMDB.Models.Collections;
using NoMercy.Providers.TMDB.Models.Movies;
using NoMercy.Storage;

namespace NoMercy.Tests.MediaProcessing.Collections;

[Trait("Category", "Unit")]
public class CollectionManagerTests
{
    private const int UnknownToTmdb = 7;

    /// <summary>
    /// The parts of a collection are fetched in parallel. They were gathered
    /// in a plain List, which loses entries or throws when two fetches finish
    /// at once (#366). Every part TMDB knows must come back, in the order of
    /// the collection.
    /// </summary>
    [Fact]
    public async Task Every_part_tmdb_knows_is_fetched_in_collection_order()
    {
        TmdbCollectionAppends collection = new()
        {
            Parts = [.. Enumerable.Range(1, 2000).Select(id => new TmdbMovie { Id = id })],
        };

        List<TmdbMovieAppends> fetched = await Manager().FetchPartsAsync(collection);

        fetched
            .Select(movie => movie.Id)
            .Should()
            .Equal(Enumerable.Range(1, 2000).Where(id => id != UnknownToTmdb));
    }

    private static CollectionManager Manager()
    {
        MovieManager movieManager = new(
            Mock.Of<IMovieRepository>(),
            new Mock<JobDispatcher>().Object,
            Mock.Of<IStorageFactory>(),
            NullLogger<MovieManager>.Instance,
            Mock.Of<IAnimeEnrichmentService>()
        );

        return new(
            Mock.Of<ICollectionRepository>(),
            movieManager,
            new Mock<JobDispatcher>().Object,
            NullLogger<CollectionManager>.Instance,
            Client
        );
    }

    private static ITmdbMovieClient Client(int movieId)
    {
        Mock<ITmdbMovieClient> client = new();
        client
            .Setup(c => c.WithAllAppends(It.IsAny<bool?>()))
            .Returns(async () =>
            {
                // Finish on another thread, as a real fetch does, so the
                // parallel loop really runs the parts side by side.
                await Task.Yield();
                return movieId == UnknownToTmdb ? null : new TmdbMovieAppends { Id = movieId };
            });

        return client.Object;
    }
}
