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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NoMercy.Data.Services;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.NmSystem.Dto;
using NoMercy.Providers.AcoustId;
using NoMercy.Storage;
using NoMercyQueue;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.Data.Jobs;

[Serializable]
public class MusicJob : IShouldQueue, IJobStorageInjector, IDisposable, IAsyncDisposable
{
    [JsonIgnore]
    public ILoggerFactory LoggerFactory { get; set; } = null!;

    [JsonIgnore]
    private ILogger Log => field ??= LoggerFactory.CreateLogger(GetType());

    public string QueueName => "import";
    public int Priority => 5;

    public string? Folder { get; set; }
    public Library? Library { get; set; }

    [JsonIgnore]
    public IStorageFactory StorageFactory { get; set; } = null!;

    [JsonIgnore]
    public IStorageDriver storageDriver { get; set; } = null!;

    [JsonIgnore]
    public IAudioFingerprinter AudioFingerprinter { get; set; } = null!;

    private ILogger<MusicLogic> _musicLogicLogger = null!;

    private IDbContextFactory<MediaContext> _mediaContextFactory = null!;

    // Constructor injection: the queue worker builds the job via
    // ActivatorUtilities; [ActivatorUtilitiesConstructor] selects this ctor
    // over the serialized-data ctor. The parameterless ctor is kept for
    // deserialization, and InjectStorageServices remains as a fallback.
    [ActivatorUtilitiesConstructor]
    public MusicJob(
        ILoggerFactory loggerFactory,
        IStorageFactory storageFactory,
        IStorageDriver storageDriver,
        IAudioFingerprinter audioFingerprinter,
        ILogger<MusicLogic> musicLogicLogger,
        IDbContextFactory<MediaContext> mediaContextFactory
    )
    {
        LoggerFactory = loggerFactory;
        StorageFactory = storageFactory;
        this.storageDriver = storageDriver;
        AudioFingerprinter = audioFingerprinter;
        _musicLogicLogger = musicLogicLogger;
        _mediaContextFactory = mediaContextFactory;
    }

    public MusicJob()
    {
        //
    }

    public MusicJob(string folder, Library library)
    {
        Folder = folder;
        Library = library;
    }

    public void InjectStorageServices(IServiceProvider serviceProvider)
    {
        LoggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        StorageFactory = serviceProvider.GetRequiredService<IStorageFactory>();
        storageDriver = serviceProvider.GetRequiredService<IStorageDriver>();
        AudioFingerprinter = serviceProvider.GetRequiredService<IAudioFingerprinter>();
        _musicLogicLogger = serviceProvider.GetRequiredService<ILogger<MusicLogic>>();
        _mediaContextFactory = serviceProvider.GetRequiredService<
            IDbContextFactory<MediaContext>
        >();
    }

    public async Task Handle()
    {
        if (Folder is null)
            return;
        if (Library is null)
            return;

        await using MediaScan mediaScan = new(storageDriver);
        IEnumerable<MediaFolderExtend> mediaFolder = await mediaScan
            .EnableFileListing()
            .DisableRegexFilter()
            .Process(Folder, 20);

        foreach (MediaFolderExtend list in mediaFolder)
        {
            Log.LogInformation("Music {Path}: Processing", list.Path);

            MusicLogic music = new(
                _musicLogicLogger,
                Library,
                list,
                _mediaContextFactory,
                StorageFactory,
                AudioFingerprinter
            );
            await music.Process();
        }
    }

    public void Dispose()
    {
        //
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
