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

namespace NoMercyQueue.Core.Models;

public class QueueJobModel
{
    public int Id { get; set; }
    public int Priority { get; set; }
    public string Queue { get; set; } = "default";
    public required string Payload { get; set; }
    public byte Attempts { get; set; }

    /// <summary>
    /// Hash of the payload this job was first enqueued with. Set once and never
    /// rewritten, so a handle given out at dispatch stays valid after the payload
    /// changes. Empty on rows from before the column existed.
    /// </summary>
    public string OriginalPayloadHash { get; set; } = string.Empty;

    /// <summary>
    /// How many times this job lost its worker to the process going away rather
    /// than to anything the job did. Counted separately from
    /// <see cref="Attempts"/> so a restart never retires a healthy job, while a
    /// job that kills the process on every run still converges.
    /// </summary>
    public byte Interruptions { get; set; }
    public DateTime? ReservedAt { get; set; }
    public DateTime AvailableAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// ID of the coordinator job that spawned this child task.
    /// Null for top-level (non-decomposed) jobs.
    /// </summary>
    public int? ParentJobId { get; set; }

    /// <summary>
    /// Shared ULID tag grouping all tasks from one coordinator encode run.
    /// Null for non-decomposed jobs.
    /// </summary>
    public string? GroupTag { get; set; }

    /// <summary>
    /// Key of the shared input this job reads, for jobs whose input is too big to
    /// copy into every payload. Null for jobs that carry their own input.
    /// </summary>
    public string? SharedInputKey { get; set; }
}
