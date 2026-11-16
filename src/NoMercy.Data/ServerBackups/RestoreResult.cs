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
using Newtonsoft.Json.Converters;

namespace NoMercy.Data.ServerBackups;

/// <summary>How a restore ended, kept so the dashboard can show it after the restart.</summary>
public sealed class RestoreResult
{
    [JsonProperty("backup_id")]
    public string BackupId { get; set; } = string.Empty;

    [JsonProperty("status")]
    [JsonConverter(typeof(StringEnumConverter))]
    public RestoreStatus Status { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("finished_at")]
    public DateTime FinishedAt { get; set; }

    [JsonProperty("safety_backup_id")]
    public string? SafetyBackupId { get; set; }

    /// <summary>
    /// The set holding the files the restore replaced, as the old server left
    /// them. Null when the restore did not apply, or when that set could not be
    /// written (the message then says where the files are).
    /// </summary>
    [JsonProperty("pre_restore_backup_id")]
    public string? PreRestoreBackupId { get; set; }
}
