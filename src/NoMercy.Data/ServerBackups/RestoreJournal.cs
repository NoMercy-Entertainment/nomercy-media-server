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
/// Written to disk before the first live file moves and deleted once the last
/// one is in place. Finding it at start means a swap was cut off, and every step
/// is undone.
/// </summary>
public sealed class RestoreJournal
{
    public const string FileName = "journal.json";

    [JsonProperty("backup_id")]
    public string BackupId { get; set; } = string.Empty;

    [JsonProperty("safety_backup_id")]
    public string? SafetyBackupId { get; set; }

    [JsonProperty("started_at")]
    public DateTime StartedAt { get; set; }

    [JsonProperty("steps")]
    public List<RestoreJournalStep> Steps { get; set; } = [];
}
