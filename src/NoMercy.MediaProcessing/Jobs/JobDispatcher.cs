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

using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.MediaProcessing.Images.Palettes;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.MediaProcessing.Jobs.PaletteJobs;
using NoMercy.Providers.MusicBrainz.Models;
using NoMercyQueue;
using NoMercyQueue.Core.Interfaces;
using QueueJobDispatcher = NoMercyQueue.JobDispatcher;

namespace NoMercy.MediaProcessing.Jobs;

public class JobDispatcher : IJobDispatcher
{
    public JobDispatcher(QueueJobDispatcher dispatcher)
    {
        Dispatcher = dispatcher;
    }

    public JobDispatcher()
    {
        Dispatcher = QueueRunner.Current?.Dispatcher;
    }

    public void Dispatch(IShouldQueue job)
    {
        Dispatcher.Dispatch(job);
    }

    public virtual void Dispatch(IShouldQueue job, string onQueue, int priority)
    {
        Dispatcher.Dispatch(job, onQueue, priority);
    }

    /// <summary>
    /// Queue a job and say which one it became, for a caller that has to follow
    /// it afterwards. Null when an identical payload is already queued.
    /// </summary>
    public virtual string? DispatchTracked(IShouldQueue job)
    {
        return Dispatcher.DispatchTracked(job, job.QueueName, job.Priority);
    }

    public void DispatchChild(
        IShouldQueue job,
        string onQueue,
        int priority,
        int parentJobId,
        string groupTag
    )
    {
        Dispatcher.DispatchChild(job, onQueue, priority, parentJobId, groupTag);
    }

    private QueueJobDispatcher Dispatcher =>
        field
        ?? throw new InvalidOperationException(
            "JobDispatcher requires a QueueRunner instance. Ensure QueueRunner is initialized before dispatching jobs."
        );

    public virtual void DispatchJob<TJob>(
        Ulid libraryId,
        Ulid folderId,
        string id,
        string inputFile,
        Ulid? sourceDriverId = null
    )
        where TJob : AbstractEncoderJob, new()
    {
        TJob job = new()
        {
            LibraryId = libraryId,
            FolderId = folderId,
            Id = id,
            InputFile = inputFile,
            SourceDriverId = sourceDriverId,
        };
        Dispatcher.Dispatch(job);
    }

    public virtual void DispatchJob<TJob>(
        Ulid libraryId,
        Ulid folderId,
        Guid releaseId,
        string filePath
    )
        where TJob : AbstractMusicFolderJob, new()
    {
        TJob job = new()
        {
            LibraryId = libraryId,
            FolderId = folderId,
            ReleaseId = releaseId,
            InputFolder = filePath,
        };
        Dispatcher.Dispatch(job);
    }

    public virtual void DispatchJob<TJob>(Ulid libraryId, Ulid folderId, string filePath)
        where TJob : AbstractMusicFolderJob, new()
    {
        TJob job = new()
        {
            LibraryId = libraryId,
            FolderId = folderId,
            InputFolder = filePath,
        };
        Dispatcher.Dispatch(job);
    }

    public virtual void DispatchJob<TJob>(int id, Ulid libraryId)
        where TJob : AbstractMediaJob, new()
    {
        TJob job = new() { Id = id, LibraryId = libraryId };
        Dispatcher.Dispatch(job);
    }

    /// <summary>
    /// A job the caller has already filled in.
    /// <para>
    /// The overloads around this one each name the two or three fields their
    /// callers happened to need, which works until a job carries something they
    /// do not know about - and then the choice is a new overload per field or a
    /// caller that cannot say what it means.
    /// </para>
    /// </summary>
    public virtual void DispatchJob<TJob>(TJob job)
        where TJob : AbstractMediaJob
    {
        Dispatcher.Dispatch(job);
    }

    public virtual void DispatchJob<TJob>(Ulid libraryId)
        where TJob : AbstractMediaJob, new()
    {
        TJob job = new() { LibraryId = libraryId };
        Dispatcher.Dispatch(job);
    }

    public virtual void DispatchJob<TJob>(int id, Library library, int? priority = null)
        where TJob : AbstractMediaJob, new()
    {
        TJob job = new() { Id = id, LibraryId = library.Id };
        Dispatcher.Dispatch(job, job.QueueName, priority ?? job.Priority);
    }

    internal virtual void DispatchJob<TJob, TChild>(TChild data)
        where TJob : AbstractMediaExraDataJob<TChild>, new()
    {
        TJob job = new() { Storage = data };
        Dispatcher.Dispatch(job);
    }

    public virtual void DispatchJob<TJob, TChild>(IEnumerable<TChild> data, string name)
        where TJob : AbstractShowExtraDataJob<TChild, string>, new()
    {
        TJob job = new() { Storage = data, Name = name };
        Dispatcher.Dispatch(job);
    }

    public void DispatchJob<TJob>()
        where TJob : AbstractJob, new()
    {
        TJob job = new();
        Dispatcher.Dispatch(job, job.QueueName, job.Priority);
    }

    public virtual void DispatchJob<TJob>(string baseFolderPath, Ulid libraryId)
        where TJob : AbstractMusicFolderJob, new()
    {
        TJob job = new() { InputFolder = baseFolderPath, LibraryId = libraryId };
        Dispatcher.Dispatch(job);
    }

    public virtual void DispatchJob<TJob>(MusicBrainzReleaseGroup musicBrainzReleaseGroup)
        where TJob : MusicMetadataJob, new()
    {
        TJob job = new() { ReleaseGroupId = musicBrainzReleaseGroup.Id };
        Dispatcher.Dispatch(job);
    }

    public virtual void DispatchJob<TJob>(MusicBrainzArtist musicBrainzArtist)
        where TJob : MusicMetadataJob, new()
    {
        TJob job = new() { ArtistId = musicBrainzArtist.Id };
        Dispatcher.Dispatch(job);
    }

    public virtual void DispatchColorPaletteJob(
        string entityType,
        string entityId,
        int? priority = null
    )
    {
        if (!NeedsPalette(entityType, entityId))
            return;

        ColorPaletteJob job = new(entityType, entityId);
        Dispatch(job, "palette", priority ?? job.Priority);
    }

    /// <summary>
    /// True when the entity has no usable palette yet. Every caller dispatches one
    /// of these per cast member per stored title, and people are shared across
    /// titles, so the overwhelming majority target an entity that was painted long
    /// ago. <see cref="ColorPaletteJob"/> already early-returns in that case, which
    /// makes the queued job a guaranteed no-op that still costs a dedup query, an
    /// insert, a reserve, a deserialize and a delete. Measured on a real library:
    /// ~3.6k palette jobs inserted per minute against 128,612 of 130,865 people
    /// already painted — the queue grew ~1000x faster than it drained. Answering
    /// the same question here, before the row exists, keeps the queue for real work.
    /// <para>Failing open (returning true) is deliberate: a broken check must queue
    /// the job and let it decide, never silently drop a palette.</para>
    /// </summary>
    protected virtual bool NeedsPalette(string entityType, string entityId)
    {
        IPaletteSource? source = DefaultPaletteSourceRegistry.Instance.Resolve(entityType);
        if (source is null)
            return true;

        try
        {
            using MediaContext db = new();
            string? current = source
                .CurrentPaletteAsync(db, entityId, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            return !ColorPalette.IsFinished(current);
        }
        catch
        {
            return true;
        }
    }
}
