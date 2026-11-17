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
/// One swap of the restore: the live entry moves aside to <see cref="Previous"/>,
/// then the staged entry (if any) moves into its place. Both are renames, and
/// which of them happened can be read back from what exists on disk, so a swap
/// can be undone after a crash.
/// </summary>
public sealed class RestoreJournalStep
{
    [JsonProperty("live")]
    public string Live { get; set; } = string.Empty;

    [JsonProperty("previous")]
    public string Previous { get; set; } = string.Empty;

    /// <summary>Null for a file that only moves aside, such as a stale <c>-wal</c>.</summary>
    [JsonProperty("staged")]
    public string? Staged { get; set; }

    [JsonProperty("is_directory")]
    public bool IsDirectory { get; set; }
}
