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
using Newtonsoft.Json;
using NoMercy.PluginSdk.Hooks;
using NoMercy.Storage;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.MediaProcessing.Jobs.MediaJobs;

// ---------------------------------------------------------------------------------------------------------------------
// Code
// ---------------------------------------------------------------------------------------------------------------------
[Serializable]
public abstract class AbstractMediaExraDataJob<T> : IShouldQueue
{
    protected AbstractMediaExraDataJob() { }

    protected AbstractMediaExraDataJob(
        IStorageFactory storageFactory,
        IStorageDriver storageDriver,
        ILoggerFactory loggerFactory
    )
    {
        StorageFactory = storageFactory;
        StorageDriver = storageDriver;
        LoggerFactory = loggerFactory;
    }

    public abstract string QueueName { get; }
    public abstract int Priority { get; }

    private T? _storage;

    public T Storage
    {
        get => _storage ??= default!;
        set => _storage = value;
    }

    [JsonIgnore]
    public IStorageFactory StorageFactory { get; private set; } = null!;

    [JsonIgnore]
    public IStorageDriver StorageDriver { get; private set; } = null!;

    [JsonIgnore]
    public ILoggerFactory LoggerFactory { get; private set; } = null!;

    /// <summary>
    /// What plugins contribute to a scan, and what they know about a title.
    /// Pulled from the scope like every other job dependency, because these jobs
    /// build their managers by hand and a manager that only ever receives a
    /// dispatcher through the container is a hook wired everywhere except where
    /// the work happens.
    /// </summary>
    [JsonIgnore]
    public IPluginMediaSourceProvider PluginMediaSources { get; private set; } = null!;

    [JsonIgnore]
    public IPluginMetadataResolver PluginMetadata { get; private set; } = null!;

    public abstract Task Handle();

    public void Dispose()
    {
        _storage = default;
    }
}
