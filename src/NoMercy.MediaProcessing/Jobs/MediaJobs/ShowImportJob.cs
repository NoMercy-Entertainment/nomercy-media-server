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

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.TvShows;
using NoMercy.Events;
using NoMercy.Events.Library;
using NoMercy.Events.Media;
using NoMercy.MediaProcessing.Common;
using NoMercy.MediaProcessing.Episodes;
using NoMercy.MediaProcessing.Files;
using NoMercy.MediaProcessing.Seasons;
using NoMercy.MediaProcessing.Shows;
using NoMercy.NmSystem;
using NoMercy.Providers.AniList;
using NoMercy.Providers.Jikan;
using NoMercy.Providers.TMDB.Models.Season;
using NoMercy.Providers.TMDB.Models.TV;
using NoMercy.Storage;

namespace NoMercy.MediaProcessing.Jobs.MediaJobs;

// ---------------------------------------------------------------------------------------------------------------------
// Code
// ---------------------------------------------------------------------------------------------------------------------
[Serializable]
public class ShowImportJob : AbstractMediaJob
{
    public ShowImportJob() { }

    public ShowImportJob(
        IStorageFactory storageFactory,
        IStorageDriver storageDriver,
        ILoggerFactory loggerFactory
    )
        : base(storageFactory, storageDriver, loggerFactory) { }

    public override string QueueName => "import";
    public override int Priority => 5;

    public bool HighPriority { get; set; }

    /// <summary>
    /// Why this show is being imported. Defaults to the file origin, so a
    /// dispatch site that says nothing is treated as a scan rather than as
    /// something the owner asked for.
    /// </summary>
    public string AddedBy { get; set; } = LibraryLinkOrigin.File;

    /// <summary>
    /// Files picked in Add content for this show, to encode once the import has stored
    /// its episodes. Empty for every other dispatch, and absent from payloads queued
    /// before this property existed, which deserialize to empty and run as before.
    /// </summary>
    public List<EncodeAfterImportFile> EncodeAfterImport { get; set; } = [];

    /// <summary>Queues one encode per file carried by this import.</summary>
    internal void DispatchEncodes(IJobDispatcher jobDispatcher)
    {
        foreach (EncodeAfterImportFile file in EncodeAfterImport)
        {
            VideoEncodeJob job = new()
            {
                LibraryId = LibraryId,
                FolderId = file.FolderId,
                Id = file.Id,
                InputFile = file.InputFile,
                SourceDriverId = file.SourceDriverId,
                PresetId = file.PresetId,
            };
            jobDispatcher.Dispatch(job, job.QueueName, job.Priority);
        }
    }

    public override async Task Handle()
    {
        await using MediaContext context = new();
        JobDispatcher jobDispatcher = new();

        ShowRepository showRepository = new(context);
        MediaTypeClassifier mediaTypeClassifier = new(
            new AniListMetadataProvider(),
            new JikanMetadataProvider()
        );
        AnimeEnrichmentService animeEnrichmentService = new(
            mediaTypeClassifier,
            new AniListMetadataProvider(),
            new JikanMetadataProvider(),
            showRepository,
            new NoMercy.MediaProcessing.Movies.MovieRepository(context)
        );
        ShowManager showManager = new(
            showRepository,
            jobDispatcher,
            StorageFactory,
            mediaTypeClassifier,
            animeEnrichmentService,
            LoggerFactory.CreateLogger<ShowManager>()
        );

        SeasonRepository seasonRepository = new(context);
        SeasonManager seasonManager = new(
            seasonRepository,
            jobDispatcher,
            LoggerFactory.CreateLogger<SeasonManager>()
        );

        EpisodeRepository episodeRepository = new(context);
        EpisodeManager episodeManager = new(
            episodeRepository,
            jobDispatcher,
            LoggerFactory.CreateLogger<EpisodeManager>()
        );

        Library tvLibrary = await context
            .Libraries.Where(f => f.Id == LibraryId)
            .Include(f => f.FolderLibraries)
                .ThenInclude(f => f.Folder)
            .FirstAsync();

        bool wasEmpty = !await context.LibraryTv.AnyAsync(lt => lt.LibraryId == LibraryId);

        TmdbTvShowAppends? show = await ImportAndPublishAsync(
            context,
            () => showManager.AddShowAsync(Id, tvLibrary, HighPriority, AddedBy),
            EventBusProvider.IsConfigured ? EventBusProvider.Current : null
        );
        if (show == null)
        {
            await ImportFailureRecorder.RecordAsync(
                context,
                "ShowImportJob",
                Id.ToString(),
                LibraryId,
                "TMDB show metadata fetch returned no result after retries."
            );
            return;
        }

        IEnumerable<TmdbSeasonAppends> seasons = await seasonManager.StoreSeasonsAsync(
            show,
            HighPriority
        );

        ConcurrentBag<Episode> episodes = [];
        await Parallel.ForEachAsync(
            seasons,
            SystemParallelism.Options,
            async (season, _) =>
            {
                IEnumerable<Episode> eps = await episodeManager.Add(show, season, HighPriority);
                foreach (Episode episode in eps)
                {
                    episodes.Add(episode);
                }
            }
        );

        await episodeRepository.StoreEpisodes(episodes);

        jobDispatcher.DispatchJob<FileRescanJob>(Id, tvLibrary);

        DispatchEncodes(jobDispatcher);

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

    internal async Task<TmdbTvShowAppends?> ImportAndPublishAsync(
        MediaContext context,
        Func<Task<TmdbTvShowAppends?>> add,
        IEventBus? eventBus
    )
    {
        bool isNewToLibrary =
            !await context.Tvs.AsNoTracking().AnyAsync(tv => tv.Id == Id && tv.LibraryId != default)
            && !await context.LibraryTv.AsNoTracking().AnyAsync(link => link.TvId == Id);
        TmdbTvShowAppends? show = await add();
        if (isNewToLibrary && show is not null && eventBus is not null)
            await eventBus.PublishAsync(
                new MediaAddedEvent
                {
                    MediaId = Id,
                    MediaType = "tvshow",
                    Title = show.Name,
                    LibraryId = LibraryId,
                }
            );

        return show;
    }
}
