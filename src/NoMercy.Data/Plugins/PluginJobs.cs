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

using NoMercy.PluginSdk.Abstractions;
using NoMercyQueue.Core.Interfaces;
using NoMercyQueue.Core.Models;

namespace NoMercy.Data.Plugins;

/// <summary>
/// The server side of <see cref="IPluginJobs" />.
///
/// <para>
/// The id a plugin holds is the job's payload hash, not the queue's row id, and
/// that is the whole reason this can answer at all. A row id does not survive:
/// a job that succeeds is deleted from the queue, and a job that fails is
/// deleted and rewritten into the failed table under a new identity. The payload
/// is what both rows have in common.
/// </para>
///
/// <para>
/// So: still in the queue means queued or running; in the failed table means
/// failed, with the reason; in neither means it ran and was cleared, which is
/// the only thing "gone from both" can be for work that was definitely queued.
/// </para>
/// </summary>
public class PluginJobs(IQueueContext queue) : IPluginJobs
{
    public Task<PluginJobStatus?> StatusAsync(string jobId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return Task.FromResult<PluginJobStatus?>(null);

        QueueJobModel? queued = queue.FindJobByPayloadHash(jobId);

        if (queued is not null)
        {
            // Reserved means a worker has taken it. Until then it is waiting,
            // and the difference is what tells an owner "nothing is happening
            // yet" from "it is happening now".
            PluginJobState state = queued.ReservedAt is null
                ? PluginJobState.Queued
                : PluginJobState.Running;

            return Task.FromResult<PluginJobStatus?>(new(jobId, state, null, null));
        }

        FailedJobModel? failed = queue.FindFailedJobByPayloadHash(jobId);

        if (failed is not null)
        {
            return Task.FromResult<PluginJobStatus?>(
                new(jobId, PluginJobState.Failed, failed.Exception, failed.FailedAt)
            );
        }

        // In neither table. The work was queued - a plugin only holds an id this
        // server handed it - so it ran and its row was cleared.
        return Task.FromResult<PluginJobStatus?>(new(jobId, PluginJobState.Finished, null, null));
    }
}
