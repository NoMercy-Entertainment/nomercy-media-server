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
/// Written just before the commit point of a restore and kept until the files
/// the swap replaced are safe in a set. Finding it at start means the restore
/// was applied; while <see cref="PreRestoreBackupId"/> is null, those files
/// still wait to be saved, and the start saves them.
/// </summary>
public sealed class RestoreCommit
{
    public const string FileName = "commit.json";

    [JsonProperty("backup_id")]
    public string BackupId { get; set; } = string.Empty;

    [JsonProperty("safety_backup_id")]
    public string? SafetyBackupId { get; set; }

    [JsonProperty("committed_at")]
    public DateTime CommittedAt { get; set; }

    /// <summary>Set once the replaced files are published as a set: the "saved" mark.</summary>
    [JsonProperty("pre_restore_backup_id")]
    public string? PreRestoreBackupId { get; set; }
}
