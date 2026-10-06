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

namespace NoMercyQueue.Core.Interfaces;

public interface IShouldQueue
{
    string QueueName { get; }
    int Priority { get; }
    Task Handle();
}

/// <summary>
/// Optional companion to <see cref="IShouldQueue"/>. When a job implements
/// this interface the queue worker calls <see cref="ReceiveJobId"/> with the
/// persisted queue-job ID immediately before invoking <see cref="IShouldQueue.Handle"/>.
///
/// Used by coordinator jobs (e.g. <c>VideoEncodeJob</c>) that need their own
/// queue-job ID to stamp child jobs with the correct <c>ParentJobId</c>.
/// </summary>
public interface IJobIdReceiver
{
    void ReceiveJobId(int jobId);
}

/// <summary>
/// Optional companion to <see cref="IShouldQueue"/>. The queue worker calls
/// <see cref="ReceiveAttempt"/> before <see cref="IShouldQueue.Handle"/> so a
/// job can tell whether a failure will be retried or is final.
/// </summary>
public interface IJobAttemptReceiver
{
    void ReceiveAttempt(int attempt, int maxAttempts);
}
