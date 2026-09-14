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

using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.MediaProcessing.Jobs;

/// <summary>
/// Media-domain job dispatch surface. Extends the low-level queue
/// <see cref="NoMercyQueue.Core.Interfaces.IJobDispatcher"/> with the
/// strongly-typed DispatchJob overloads consumed by controllers and managers.
/// </summary>
public interface IJobDispatcher : NoMercyQueue.Core.Interfaces.IJobDispatcher
{
    void DispatchJob<TJob>(Ulid libraryId, Ulid folderId, Guid releaseId, string filePath)
        where TJob : AbstractMusicFolderJob, new();

    void DispatchJob<TJob>(int id, Ulid libraryId)
        where TJob : AbstractMediaJob, new();

    /// <summary>A job the caller has already filled in.</summary>
    void DispatchJob<TJob>(TJob job)
        where TJob : AbstractMediaJob;

    /// <summary>
    /// Queue a job and say which one it became, for a caller that has to follow
    /// it afterwards. Null when an identical payload is already queued.
    /// </summary>
    string? DispatchTracked(IShouldQueue job);

    void DispatchJob<TJob>(Ulid libraryId)
        where TJob : AbstractMediaJob, new();

    void DispatchColorPaletteJob(string entityType, string entityId, int? priority = null);

    void DispatchJob<TJob, TChild>(IEnumerable<TChild> data, string name)
        where TJob : AbstractShowExtraDataJob<TChild, string>, new();

    void DispatchJob<TJob>()
        where TJob : AbstractJob, new();

    void DispatchJob<TJob>(
        Ulid libraryId,
        Ulid folderId,
        string id,
        string inputFile,
        Ulid? sourceDriverId = null
    )
        where TJob : AbstractEncoderJob, new();
}
