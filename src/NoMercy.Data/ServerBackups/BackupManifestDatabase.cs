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

/// <summary>One database of a backup set.</summary>
public sealed class BackupManifestDatabase : BackupManifestFile
{
    /// <summary><c>app</c>, <c>media</c> or <c>queue</c>.</summary>
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>The newest EF Core migration applied to the copy; null when it has none.</summary>
    [JsonProperty("last_migration")]
    public string? LastMigration { get; set; }
}
