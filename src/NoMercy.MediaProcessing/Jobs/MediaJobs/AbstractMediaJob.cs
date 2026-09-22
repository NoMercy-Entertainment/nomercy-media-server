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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NoMercy.PluginSdk.Hooks;
using NoMercy.Storage;
using NoMercyQueue;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.MediaProcessing.Jobs.MediaJobs;

// ---------------------------------------------------------------------------------------------------------------------
// Code
// ---------------------------------------------------------------------------------------------------------------------
[Serializable]
public abstract class AbstractMediaJob : IShouldQueue, IJobStorageInjector
{
    protected AbstractMediaJob() { }

    protected AbstractMediaJob(
        IStorageFactory storageFactory,
        IStorageDriver storageDriver,
        ILoggerFactory loggerFactory
    )
    {
        StorageFactory = storageFactory;
        StorageDriver = storageDriver;
        LoggerFactory = loggerFactory;
    }

    public int Id { get; set; }
    public Ulid LibraryId { get; set; }

    [JsonIgnore]
    public IStorageFactory StorageFactory { get; private set; } = null!;

    [JsonIgnore]
    public IStorageDriver StorageDriver { get; private set; } = null!;

    [JsonIgnore]
    public ILoggerFactory LoggerFactory { get; private set; } = null!;

    /// <summary>
    /// What plugins contribute to a scan, and what they know about a title.
    /// <para>
    /// Pulled from the scope like every other job dependency rather than left
    /// to the managers' own DI. These jobs build their managers by hand, so a
    /// manager that only ever receives a dispatcher through the container is a
    /// hook that is wired everywhere except where the work actually happens.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public IPluginMediaSourceProvider PluginMediaSources { get; private set; } = null!;

    [JsonIgnore]
    public IPluginMetadataResolver PluginMetadata { get; private set; } = null!;

    [JsonIgnore]
    protected ILogger Log => field ??= LoggerFactory.CreateLogger(GetType());

    public virtual void InjectStorageServices(IServiceProvider serviceProvider)
    {
        StorageFactory = serviceProvider.GetRequiredService<IStorageFactory>();
        StorageDriver = serviceProvider.GetRequiredService<IStorageDriver>();
        LoggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        PluginMediaSources = serviceProvider.GetRequiredService<IPluginMediaSourceProvider>();
        PluginMetadata = serviceProvider.GetRequiredService<IPluginMetadataResolver>();
    }

    public abstract string QueueName { get; }
    public abstract int Priority { get; }

    public abstract Task Handle();

    public void Dispose() { }
}
