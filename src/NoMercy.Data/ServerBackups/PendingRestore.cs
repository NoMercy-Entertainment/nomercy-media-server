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

namespace NoMercy.Data.ServerBackups;

/// <summary>
/// A restore that was verified and asked for, and that the next start applies
/// before anything opens the databases.
/// </summary>
public sealed class PendingRestore
{
    [JsonProperty("backup_id")]
    public string BackupId { get; set; } = string.Empty;

    [JsonProperty("requested_at")]
    public DateTime RequestedAt { get; set; }

    /// <summary>The backup of the state before this restore, to undo it with.</summary>
    [JsonProperty("safety_backup_id")]
    public string? SafetyBackupId { get; set; }
}
