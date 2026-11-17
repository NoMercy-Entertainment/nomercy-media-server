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

using Newtonsoft.Json;
using NoMercy.Data.ServerBackups;

namespace NoMercy.Api.DTOs.Dashboard;

public record RestoreResultDto
{
    [JsonProperty("backup_id")]
    public string BackupId { get; init; } = string.Empty;

    /// <summary><c>applied</c>, <c>refused</c>, <c>rolled_back</c> or <c>rollback_incomplete</c>.</summary>
    [JsonProperty("status")]
    public string Status { get; init; } = string.Empty;

    [JsonProperty("message")]
    public string Message { get; init; } = string.Empty;

    [JsonProperty("finished_at")]
    public DateTime FinishedAt { get; init; }

    [JsonProperty("safety_backup_id")]
    public string? SafetyBackupId { get; init; }

    public static RestoreResultDto From(RestoreResult result) =>
        new()
        {
            BackupId = result.BackupId,
            Status = result.Status switch
            {
                RestoreStatus.Applied => "applied",
                RestoreStatus.Refused => "refused",
                RestoreStatus.RolledBack => "rolled_back",
                _ => "rollback_incomplete",
            },
            Message = result.Message,
            FinishedAt = result.FinishedAt,
            SafetyBackupId = result.SafetyBackupId,
        };
}
