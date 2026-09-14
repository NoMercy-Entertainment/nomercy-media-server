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

using NoMercyQueue.Core.Models;

namespace NoMercy.Queue.MediaServer.Repositories;

/// <summary>
/// Queue-table (<c>queue.db</c>) reads and writes behind the dashboard's task
/// and encoder-queue panels. Every member returns domain models
/// (<see cref="QueueJobModel"/>, <see cref="FailedJobModel"/>) or small
/// records — never an EF query type or <c>QueueContext</c> itself — so the
/// controller layer stays free of direct EF access.
/// </summary>
public interface IQueueTaskRepository
{
    /// <summary>The highest-priority jobs across every queue, for the task list.</summary>
    Task<List<QueueJobModel>> GetRecentJobsAsync(
        int limit,
        CancellationToken cancellationToken = default
    );

    /// <summary>Distinct priority values currently in use on the encoder queue family.</summary>
    Task<List<int>> GetEncoderPrioritiesAsync(CancellationToken cancellationToken = default);

    /// <summary>Up to <paramref name="limit"/> encoder-family jobs at exactly <paramref name="priority"/>.</summary>
    Task<List<QueueJobModel>> GetEncoderJobsByPriorityAsync(
        int priority,
        int limit,
        CancellationToken cancellationToken = default
    );

    /// <summary>Every encoder-family job currently reserved (in flight), regardless of priority.</summary>
    Task<List<QueueJobModel>> GetRunningEncoderJobsAsync(
        int limit,
        CancellationToken cancellationToken = default
    );

    /// <summary>Tracks still queued per music release, counted over the whole queue.</summary>
    Task<Dictionary<Guid, int>> CountQueuedTracksByReleaseAsync(
        CancellationToken cancellationToken = default
    );

    /// <summary>Rows still queued on the music queue that are audio-analysis work.</summary>
    Task<int> CountQueuedMusicAnalysisAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether any row currently sits on the named queue.</summary>
    Task<bool> QueueExistsAsync(string queueName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every job on the named queue, ordered the same way the runner reserves
    /// them: priority descending, then creation order, then id.
    /// </summary>
    Task<List<QueueJobModel>> GetJobsForQueueAsync(
        string queueName,
        CancellationToken cancellationToken = default
    );

    /// <summary>Persists a new priority for each job id in <paramref name="priorityByJobId"/>.</summary>
    Task SetPrioritiesAsync(
        IReadOnlyDictionary<int, int> priorityByJobId,
        CancellationToken cancellationToken = default
    );

    /// <summary>Every recorded failed job, most recent first.</summary>
    Task<List<FailedJobModel>> GetFailedJobsAsync(CancellationToken cancellationToken = default);

    /// <summary>A single failed job by id, or null.</summary>
    Task<FailedJobModel?> FindFailedJobAsync(
        long id,
        CancellationToken cancellationToken = default
    );

    /// <summary>Removes a queue job and returns the row that was removed, or null if it no longer exists.</summary>
    Task<QueueJobModel?> DeleteJobAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Updates a queue job's priority. Returns false when the job no longer exists.</summary>
    Task<bool> UpdateJobPriorityAsync(
        int id,
        int priority,
        CancellationToken cancellationToken = default
    );

    /// <summary>Pending/running breakdown for the encoder queue family, counted in SQL.</summary>
    Task<EncoderQueueCounts> GetEncoderQueueCountsAsync(
        CancellationToken cancellationToken = default
    );

    /// <summary>Total rows currently on the encoder queue family, pending or running.</summary>
    Task<int> GetEncoderQueueDepthAsync(CancellationToken cancellationToken = default);

    /// <summary>Total rows on every queue, unfiltered.</summary>
    Task<int> GetQueueJobCountAsync(CancellationToken cancellationToken = default);

    /// <summary>Total recorded failed jobs, unfiltered.</summary>
    Task<int> GetFailedJobCountAsync(CancellationToken cancellationToken = default);
}
