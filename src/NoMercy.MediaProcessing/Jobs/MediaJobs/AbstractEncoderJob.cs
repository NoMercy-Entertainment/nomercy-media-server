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
public abstract class AbstractEncoderJob : IShouldQueue, IJobStorageInjector
{
    public string Id { get; set; } = string.Empty;
    public Ulid FolderId { get; set; }

    public Ulid LibraryId { get; set; }

    [JsonIgnore]
    public IStorageFactory StorageFactory { get; set; } = null!;

    [JsonIgnore]
    public IStorageDriver StorageDriver { get; set; } = null!;

    [JsonIgnore]
    public ILoggerFactory LoggerFactory { get; set; } = null!;

    [JsonIgnore]
    public IPluginMediaSourceProvider PluginMediaSources { get; private set; } = null!;

    [JsonIgnore]
    public IPluginMetadataResolver PluginMetadata { get; private set; } = null!;

    [JsonIgnore]
    protected ILogger Log => field ??= LoggerFactory.CreateLogger(GetType());

    public abstract string QueueName { get; }
    public abstract int Priority { get; }

    public string InputFile { get; set; } = string.Empty;

    /// <summary>
    /// When the source file lives on a remote driver (NFS / SMB / S3),
    /// this holds the driver's <see cref="Ulid"/> so <see cref="Handle"/>
    /// can acquire a local lease via the correct backend.
    /// Null means "use the destination folder's driver" (same-driver encode).
    /// </summary>
    public Ulid? SourceDriverId { get; set; }

    public abstract Task Handle();

    public void InjectStorageServices(IServiceProvider serviceProvider)
    {
        StorageFactory = serviceProvider.GetRequiredService<IStorageFactory>();
        StorageDriver = serviceProvider.GetRequiredService<IStorageDriver>();
        LoggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        PluginMediaSources = serviceProvider.GetRequiredService<IPluginMediaSourceProvider>();
        PluginMetadata = serviceProvider.GetRequiredService<IPluginMetadataResolver>();
    }

    public void Dispose() { }
}
