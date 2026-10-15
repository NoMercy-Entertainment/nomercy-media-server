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
internal class FailedJobEntity
{
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public Guid Uuid { get; set; }
    public string Connection { get; set; } = "default";

    [MaxLength(1024)]
    public required string Queue { get; set; }

    [MaxLength(4092)]
    public required string Payload { get; set; }

    [MaxLength(4092)]
    public required string Exception { get; set; }
    public DateTime FailedAt { get; set; } = DateTime.UtcNow;

    public int? ParentJobId { get; set; }

    /// <summary>Carried from the queue row that failed. Empty on older rows.</summary>
    [MaxLength(QueuePayloadHash.Length)]
    public string OriginalPayloadHash { get; set; } = string.Empty;
}
