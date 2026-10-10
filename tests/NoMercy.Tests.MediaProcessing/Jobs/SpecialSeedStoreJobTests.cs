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
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.NmSystem.Domain;

namespace NoMercy.Tests.MediaProcessing.Jobs;

[Trait("Category", "Unit")]
public class SpecialSeedStoreJobTests
{
    [Fact]
    public async Task EveryTvSeedEntryStoresItsEpisodesInSeedOrder()
    {
        await using MediaContext context = new(
            new DbContextOptionsBuilder<MediaContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options
        );

        SpecialSeedItem[] entries = McuSeedData
            .McuItems.Where(item =>
                item.Type is MediaTypes.TvMediaType or MediaTypes.AnimeMediaType
            )
            .ToArray();
        Dictionary<(string Title, int Year), int> showIds = entries
            .Select(item => (item.Title, item.Year))
            .Distinct()
            .Select((show, index) => (show, id: index + 1))
            .ToDictionary(pair => pair.show, pair => pair.id);
        Dictionary<(int TvId, int Season, int Number), int> episodeIds = new();
        List<int> expectedOrder = [];

        foreach (SpecialSeedItem entry in entries)
        {
            int tvId = showIds[(entry.Title, entry.Year)];
            int season = entry.Seasons.Single();
            int[] numbers = entry.Episodes.Length == 0 ? [1, 2] : entry.Episodes;

            foreach (int number in numbers)
            {
                (int TvId, int Season, int Number) key = (tvId, season, number);
                if (!episodeIds.TryGetValue(key, out int episodeId))
                {
                    episodeId = episodeIds.Count + 1;
                    episodeIds.Add(key, episodeId);
                    context.Episodes.Add(
                        new Episode
                        {
                            Id = episodeId,
                            TvId = tvId,
                            SeasonNumber = season,
                            EpisodeNumber = number,
                        }
                    );
                }

                expectedOrder.Add(episodeId);
            }
        }

        await context.SaveChangesAsync();

        List<SpecialItem> specialItems = [];
        foreach (SpecialSeedItem entry in entries)
        {
            SpecialSeedItem copy = new()
            {
                Index = entry.Index,
                Type = entry.Type,
                Title = entry.Title,
                Year = entry.Year,
                Seasons = [.. entry.Seasons],
                Episodes = [.. entry.Episodes],
            };
            await SpecialSeedStoreJob.AddTvEpisodes(
                context,
                copy,
                showIds[(copy.Title, copy.Year)],
                specialItems
            );
        }

        specialItems
            .Select(item => item.EpisodeId)
            .Should()
            .Equal(expectedOrder.Select(id => (int?)id));
        specialItems
            .Select(item => item.Order)
            .Should()
            .Equal(Enumerable.Range(0, expectedOrder.Count));
    }
}
