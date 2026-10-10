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

using Microsoft.EntityFrameworkCore;
using NoMercy.Data.Data;
using NoMercy.Database;
using NoMercy.Database.Models.TvShows;
using NoMercy.MediaProcessing.Jobs.Dto;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Providers.TMDB.Client;
using NoMercy.Providers.TMDB.Models.Movies;
using NoMercy.Providers.TMDB.Models.Shared;
using NoMercy.Providers.TMDB.Models.TV;
using Serilog.Events;

namespace NoMercy.MediaProcessing.Jobs.MediaJobs;

public class SpecialSeedStoreJob : AbstractJob
{
    public override string QueueName => "import";
    public override int Priority => 4;

    public SpecialSeedStoreJob()
    {
        //
    }

    public override async Task Handle()
    {
        Logger.Setup("Adding Special");

        try
        {
            await using MediaContext mediaContext = new();

            TmdbSearchClient client = new();
            List<int> movieIds = [];
            List<SpecialItem> specialItems = [];

            foreach (SpecialSeedItem item in McuSeedData.McuItems)
            {
                switch (item.Type)
                {
                    case MediaTypes.MovieMediaType:
                        await AddMovieItem(client, item, movieIds, specialItems);
                        break;
                    case MediaTypes.TvMediaType:
                    case MediaTypes.AnimeMediaType:
                        await AddTvItem(mediaContext, client, item, specialItems);
                        break;
                }
            }

            await UpsertSpecialItems(mediaContext, specialItems);
        }
        catch (Exception e)
        {
            Logger.Setup(e.Message, LogEventLevel.Fatal);
            throw;
        }
    }

    private static async Task AddMovieItem(
        TmdbSearchClient client,
        SpecialSeedItem item,
        List<int> movieIds,
        List<SpecialItem> specialItems
    )
    {
        Logger.Setup($"Searching for {item.Title} ({item.Year})");

        TmdbPaginatedResponse<TmdbMovie>? result = await client.Movie(
            item.Title,
            item.Year.ToString()
        );

        TmdbMovie? movie = result?.Results.FirstOrDefault(r =>
            !r.Title.ToLower().Contains("making of")
        );

        if (movie is null || movieIds.Contains(movie.Id))
            return;

        movieIds.Add(movie.Id);

        specialItems.Add(
            new()
            {
                SpecialId = McuSeedData.Special.Id,
                MovieId = movie.Id,
                Order = specialItems.Count,
            }
        );
    }

    private static async Task AddTvItem(
        MediaContext context,
        TmdbSearchClient client,
        SpecialSeedItem item,
        List<SpecialItem> specialItems
    )
    {
        Logger.Setup($"Searching for {item.Title} ({item.Year})");

        TmdbPaginatedResponse<TmdbTvShow>? result = await client.TvShow(
            item.Title,
            item.Year.ToString()
        );

        TmdbTvShow? tv = result?.Results.FirstOrDefault(r =>
            !r.Name.Contains("making of", StringComparison.InvariantCultureIgnoreCase)
        );

        if (tv is null)
            return;

        await AddTvEpisodes(context, item, tv.Id, specialItems);
    }

    internal static async Task AddTvEpisodes(
        MediaContext context,
        SpecialSeedItem item,
        int tvId,
        List<SpecialItem> specialItems
    )
    {
        if (item.Episodes.Length == 0)
            item.Episodes = await context
                .Episodes.Where(x => x.TvId == tvId)
                .Where(x => x.SeasonNumber == item.Seasons.First())
                .Select(selector: x => x.EpisodeNumber)
                .ToArrayAsync();

        foreach (int episodeNumber in item.Episodes)
        {
            Episode? episode = await context.Episodes.FirstOrDefaultAsync(predicate: x =>
                x.TvId == tvId
                && x.SeasonNumber == item.Seasons.First()
                && x.EpisodeNumber == episodeNumber
            );

            if (episode is null)
                continue;

            specialItems.Add(
                item: new()
                {
                    SpecialId = McuSeedData.Special.Id,
                    EpisodeId = episode.Id,
                    Order = specialItems.Count,
                }
            );
        }
    }

    private static async Task UpsertSpecialItems(
        MediaContext context,
        List<SpecialItem> specialItems
    )
    {
        Logger.Setup($"Upsetting {specialItems.Count} SpecialItems");

        IEnumerable<SpecialItem> movies = specialItems.Where(specialItem =>
            specialItem.MovieId is not null
        );

        foreach (SpecialItem movie in movies)
            try
            {
                await context
                    .SpecialItems.Upsert(movie)
                    .On(specialItem => new { specialItem.SpecialId, specialItem.MovieId })
                    .WhenMatched(
                        (old, @new) =>
                            new()
                            {
                                SpecialId = @new.SpecialId,
                                MovieId = @new.MovieId,
                                Order = @new.Order,
                            }
                    )
                    .RunAsync();
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }

        List<SpecialItem> episodes =
        [
            .. specialItems.Where(specialItem => specialItem.EpisodeId is not null),
        ];

        foreach (SpecialItem episode in episodes)
            try
            {
                await context
                    .SpecialItems.Upsert(episode)
                    .On(specialItem => new { specialItem.SpecialId, specialItem.EpisodeId })
                    .WhenMatched(
                        (old, @new) =>
                            new()
                            {
                                SpecialId = @new.SpecialId,
                                EpisodeId = @new.EpisodeId,
                                Order = @new.Order,
                            }
                    )
                    .RunAsync();
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }

        Logger.Setup("SpecialItems Upset complete");
    }
}
