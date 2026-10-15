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

public class FailedJobModel
{
    public long Id { get; set; }
    public Guid Uuid { get; set; }
    public string Connection { get; set; } = "default";
    public required string Queue { get; set; }
    public required string Payload { get; set; }

    /// <summary>Carried over from the job that failed. Empty on older rows.</summary>
    public string OriginalPayloadHash { get; set; } = string.Empty;
    public required string Exception { get; set; }
    public DateTime FailedAt { get; set; } = DateTime.UtcNow;

    public int? ParentJobId { get; set; }
}
