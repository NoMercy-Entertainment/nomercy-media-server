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

namespace NoMercyQueue.Sqlite.Entities;

[PrimaryKey(nameof(Id))]
[Index(nameof(PayloadHash))]
[Index(nameof(OriginalPayloadHash))]
internal class QueueJobEntity
{
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int Priority { get; set; }
    public string Queue { get; set; } = "default";

    public required string Payload { get; set; }

    /// <summary>
    /// SHA-256 of <see cref="Payload"/>, carried so the enqueue dedup has a
    /// fixed-width key to index. Indexing the payload itself meant a B-tree over
    /// megabyte strings, which grew to the size of the table it indexed.
    /// </summary>
    [MaxLength(QueuePayloadHash.Length)]
    public string PayloadHash { get; set; } = string.Empty;

    /// <summary>
    /// Hash of the payload at first enqueue. Set once, never rewritten, so the id
    /// a plugin was handed at dispatch still finds this job after a re-enqueue.
    /// </summary>
    [MaxLength(QueuePayloadHash.Length)]
    public string OriginalPayloadHash { get; set; } = string.Empty;

    public byte Attempts { get; set; }
    public byte Interruptions { get; set; }
    public DateTime? ReservedAt { get; set; }
    public DateTime AvailableAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// ID of the coordinator job that spawned this child task. Null for top-level jobs.
    /// </summary>
    public int? ParentJobId { get; set; }

    /// <summary>
    /// Shared ULID tag grouping all tasks from one coordinator run. Null for top-level jobs.
    /// </summary>
    [MaxLength(64)]
    public string? GroupTag { get; set; }

    /// <summary>
    /// Key of the shared input this job reads, for jobs whose input is too big to
    /// copy into every payload.
    /// </summary>
    [MaxLength(128)]
    public string? SharedInputKey { get; set; }
}
