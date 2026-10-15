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

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using NoMercyQueue.Core;

namespace NoMercy.Database.Models.Queue;

[PrimaryKey(nameof(Id))]
// Matches the task-list sort (Priority desc, CreatedAt asc) so SQLite serves it
// from the index instead of sorting the whole growing queue table.
[Index(nameof(Priority), nameof(CreatedAt), IsDescending = [true, false])]
// Every Dispatch dedups via QueueJobs.Any(j => j.Payload == x). Without an index
// that is a full scan of the (large, history-retaining) queue table — seconds per
// enqueue, which surfaced as multi-second endpoints that dispatch a job inline.
//
// Indexed by hash rather than by the payload: indexing Payload meant a B-tree over
// the payloads themselves, and music encode payloads run to a megabyte each, so the
// index grew to the size of the table and took queue.db to 23.6GB — half of it this
// one index. The lookup still confirms against the real payload, so dedup decides
// on the same equality it always did.
[Index(nameof(PayloadHash))]
[Index(nameof(OriginalPayloadHash))]
// Serves the sweep that reclaims shared input no queued job still reads.
[Index(nameof(SharedInputKey))]
// Every worker poll reserves via (Queue, ReservedAt IS NULL, Attempts, AvailableAt)
// ordered by (Priority desc, CreatedAt, Id). The sort index above cannot serve that
// — the predicate leads with Queue — so SQLite fell back to SCAN + a temp B-tree
// over the whole table: ~2.7s per reserve at 116k rows, taken while JobQueue holds
// its global write lock, which serialises every worker on every queue. Leading with
// Queue and ReservedAt turns it into a SEARCH and lets the trailing columns satisfy
// the ORDER BY. Measured 2741ms -> 87ms on a copy of a real 2.5GB queue.db.
[Index(
    nameof(Queue),
    [nameof(ReservedAt), nameof(Priority), nameof(CreatedAt), nameof(Id)],
    IsDescending = [false, false, true, false, false]
)]
public class QueueJob
{
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int Priority { get; set; }
    public string Queue { get; set; } = "default";

    public required string Payload { get; set; }

    /// <summary>
    /// SHA-256 of <see cref="Payload"/>, written by the queue on every insert and
    /// payload rewrite. Exists only to give the enqueue dedup an indexable key —
    /// see the index note above.
    /// </summary>
    [MaxLength(QueuePayloadHash.Length)]
    public string PayloadHash { get; set; } = string.Empty;

    /// <summary>
    /// Hash of the payload at first enqueue. Set once, never rewritten, so the id
    /// a plugin was handed at dispatch still finds this job after a re-enqueue.
    /// </summary>
    [MaxLength(QueuePayloadHash.Length)]
    public string OriginalPayloadHash { get; set; } = string.Empty;

    public byte Attempts { get; set; } = 0;

    /// <summary>
    /// How many times this job lost its worker to the process going away rather
    /// than to anything the job did — a restart, a kill, a power cut.
    /// <para>Separate from <see cref="Attempts"/> on purpose: an interrupted job
    /// never got to fail, so charging it an attempt retires work that was only
    /// ever unlucky. It is still counted, because a job that takes the process
    /// down with it every time it runs would otherwise retry forever.</para>
    /// </summary>
    public byte Interruptions { get; set; } = 0;
    public DateTime? ReservedAt { get; set; }
    public DateTime AvailableAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// ID of the coordinator job that spawned this child task.
    /// Null for top-level (non-decomposed) jobs.
    /// </summary>
    public int? ParentJobId { get; set; }

    /// <summary>
    /// Shared ULID tag for all tasks spawned by a single encode coordinator run.
    /// Null for non-decomposed jobs.
    /// </summary>
    [MaxLength(64)]
    public string? GroupTag { get; set; }

    /// <summary>
    /// Key into <see cref="QueueJobBlob"/> for jobs whose input is shared rather
    /// than copied per payload. Recorded here so reclaiming unreferenced input is
    /// an anti-join against this column instead of a scan over every payload.
    /// </summary>
    [MaxLength(128)]
    public string? SharedInputKey { get; set; }
}
