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
using NoMercy.Database.Models.Libraries;
using NoMercy.Encoder.Analysis;
using NoMercy.Events;
using NoMercy.Events.Library;
using NoMercy.MediaProcessing.Files.Parsing;
using NoMercy.MediaProcessing.Libraries;
using NoMercy.NmSystem.Domain;
using NoMercy.Storage;

namespace NoMercy.MediaProcessing.Jobs.MediaJobs;

// ---------------------------------------------------------------------------------------------------------------------
// Code
// ---------------------------------------------------------------------------------------------------------------------
[Serializable]
public class FileRescanJob : AbstractMediaJob
{
    public FileRescanJob() { }

    public FileRescanJob(
        IStorageFactory storageFactory,
        IStorageDriver storageDriver,
        ILoggerFactory loggerFactory,
        IMediaAnalyzer mediaAnalyzer,
        IFilenameParserPipeline filenameParser
    )
        : base(storageFactory, storageDriver, loggerFactory)
    {
        _mediaAnalyzer = mediaAnalyzer;
        _filenameParser = filenameParser;
    }

    // Private field, not a [JsonIgnore] public property — SerializationHelper.Populate
    // (Newtonsoft) only re-hydrates public members, so this survives the queue's
    // rebuild-then-populate cycle (see QueueWorker.ExecuteWithTransientRetry) untouched.
    private readonly IMediaAnalyzer _mediaAnalyzer = null!;
    private readonly IFilenameParserPipeline _filenameParser = null!;

    public override string QueueName => "file";
    public override int Priority => 10;

    public override async Task Handle()
    {
        Log.LogInformation(
            "[FileRescanJob] Handle() entered for id={Id}, libraryId={LibraryId}",
            [Id, LibraryId]
        );

        await using MediaContext context = new();
        JobDispatcher jobDispatcher = new();

        LibraryRepository libraryRepository = new(context, StorageDriver);
        LibraryManager libraryManager = new(
            libraryRepository,
            jobDispatcher,
            context,
            StorageDriver,
            StorageFactory,
            _mediaAnalyzer,
            _filenameParser,
            LoggerFactory.CreateLogger<LibraryManager>(),
            null,
            PluginMediaSources
        );

        Library? library = await libraryManager.RescanFiles(LibraryId, Id);

        string type = library?.Type switch
        {
            MediaTypes.TvMediaType or MediaTypes.AnimeMediaType => "tv",
            MediaTypes.MovieMediaType => "movie",
            MediaTypes.MusicMediaType => "music",
            _ => "unknown",
        };

        if (EventBusProvider.IsConfigured)
        {
            // Music is rescanned per library, not per title, so there is no id to
            // scope the key to — Id is 0 here and ["music", "0"] matches nothing
            // the client ever asked for.
            await EventBusProvider.Current.PublishAsync(
                new LibraryRefreshedEvent
                {
                    QueryKey = type == "music" ? [type] : [type, Id.ToString()],
                }
            );

            await EventBusProvider.Current.PublishAsync(
                new LibraryRefreshedEvent { QueryKey = ["libraries", LibraryId.ToString()] }
            );

            await EventBusProvider.Current.PublishAsync(
                new LibraryRefreshedEvent { QueryKey = ["home"] }
            );

            await EventBusProvider.Current.PublishAsync(
                new MediaFilesScannedEvent { MediaId = Id, LibraryId = LibraryId }
            );
        }
    }
}
