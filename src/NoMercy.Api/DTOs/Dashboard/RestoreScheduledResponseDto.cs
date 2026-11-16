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

namespace NoMercy.Api.DTOs.Dashboard;

public record RestoreScheduledResponseDto
{
    [JsonProperty("status")]
    public string Status { get; init; } = "restarting";

    [JsonProperty("backup_id")]
    public string BackupId { get; init; } = string.Empty;

    /// <summary>The backup of the state before the restore, to undo it with.</summary>
    [JsonProperty("safety_backup_id")]
    public string? SafetyBackupId { get; init; }

    /// <summary>Who brings the server back: <c>supervisor</c> or <c>relaunch</c>.</summary>
    [JsonProperty("mode")]
    public string Mode { get; init; } = string.Empty;
}
