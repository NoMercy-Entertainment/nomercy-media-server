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

public record PendingRestoreDto
{
    [JsonProperty("backup_id")]
    public string BackupId { get; init; } = string.Empty;

    [JsonProperty("requested_at")]
    public DateTime RequestedAt { get; init; }

    [JsonProperty("safety_backup_id")]
    public string? SafetyBackupId { get; init; }

    public static PendingRestoreDto From(PendingRestore pending) =>
        new()
        {
            BackupId = pending.BackupId,
            RequestedAt = pending.RequestedAt,
            SafetyBackupId = pending.SafetyBackupId,
        };
}
