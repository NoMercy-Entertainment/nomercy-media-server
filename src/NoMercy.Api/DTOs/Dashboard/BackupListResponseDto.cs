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

public record BackupListResponseDto
{
    /// <summary>Newest first.</summary>
    [JsonProperty("backups")]
    public List<BackupSetDto> Backups { get; init; } = [];

    /// <summary>A restore waiting for the restart, if any.</summary>
    [JsonProperty("pending_restore")]
    public PendingRestoreDto? PendingRestore { get; init; }

    /// <summary>How the last restore ended, if one was ever tried.</summary>
    [JsonProperty("last_restore")]
    public RestoreResultDto? LastRestore { get; init; }
}
