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
using NoMercy.Events;
using NoMercy.Events.Media;
using NoMercy.Providers.AcoustId;
using NoMercy.Storage;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.MediaProcessing.Jobs.MediaJobs;

// ---------------------------------------------------------------------------------------------------------------------
// Code
// ---------------------------------------------------------------------------------------------------------------------
[Serializable]
public abstract class AbstractMusicFolderJob : IShouldQueue, IJobAttemptReceiver
{
    private int _attempt = 1;
    private int _maxAttempts = 1;

    protected AbstractMusicFolderJob() { }

    protected AbstractMusicFolderJob(
        IStorageFactory storageFactory,
        IStorageDriver storageDriver,
        IAudioFingerprinter audioFingerprinter,
        ILoggerFactory loggerFactory
    )
    {
        StorageFactory = storageFactory;
        StorageDriver = storageDriver;
        AudioFingerprinter = audioFingerprinter;
        LoggerFactory = loggerFactory;
    }

    public string InputFolder { get; set; } = string.Empty;
    public Ulid LibraryId { get; set; }
    public Ulid FolderId { get; set; }
    public Guid ReleaseId { get; set; }

    [JsonIgnore]
    public IStorageFactory StorageFactory { get; private set; } = null!;

    [JsonIgnore]
    public IStorageDriver StorageDriver { get; private set; } = null!;

    [JsonIgnore]
    public IAudioFingerprinter AudioFingerprinter { get; private set; } = null!;

    [JsonIgnore]
    public ILoggerFactory LoggerFactory { get; private set; } = null!;

    [JsonIgnore]
    protected ILogger Log => field ??= LoggerFactory.CreateLogger(GetType());

    public abstract string QueueName { get; }
    public abstract int Priority { get; }

    public abstract Task Handle();

    public void ReceiveAttempt(int attempt, int maxAttempts)
    {
        _attempt = attempt;
        _maxAttempts = maxAttempts;
    }

    /// <summary>
    /// Runs an import and tells whoever follows the scan how it ended: the
    /// items it handed on, or a failure. An exception is reported only when no
    /// retry follows, so a retried job still counts once.
    /// </summary>
    protected async Task HandleWithFinishEventAsync(Func<Task<int>> import)
    {
        int added;
        try
        {
            added = await import();
        }
        catch
        {
            if (_attempt >= _maxAttempts)
                await PublishImportFinishedAsync(0, 1);
            throw;
        }

        await PublishImportFinishedAsync(added, 0);
    }

    // QueueWorker can run Handle again in the same reservation after a
    // transient SQLite error, so a job reports its finish once at most.
    private bool _finishPublished;

    private async Task PublishImportFinishedAsync(int added, int failed)
    {
        if (_finishPublished || !EventBusProvider.IsConfigured)
            return;
        _finishPublished = true;

        await EventBusProvider.Current.PublishAsync(
            new MediaImportFinishedEvent
            {
                LibraryId = LibraryId,
                Added = added,
                Failed = failed,
            }
        );
    }

    public void Dispose() { }
}
