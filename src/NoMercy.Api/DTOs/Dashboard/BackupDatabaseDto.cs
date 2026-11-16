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

public record BackupDatabaseDto
{
    [JsonProperty("name")]
    public string Name { get; init; } = string.Empty;

    [JsonProperty("size")]
    public long Size { get; init; }

    [JsonProperty("last_migration")]
    public string? LastMigration { get; init; }

    /// <summary>
    /// True when this copy failed its integrity check and was kept anyway (only in
    /// a set made before a restore). A set with a damaged database cannot be restored.
    /// </summary>
    [JsonProperty("damaged")]
    public bool Damaged { get; init; }

    [JsonProperty("damage")]
    public string? Damage { get; init; }
}
