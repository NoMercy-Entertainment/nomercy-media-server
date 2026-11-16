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

public record BackupSetDto
{
    [JsonProperty("id")]
    public string Id { get; init; } = string.Empty;

    [JsonProperty("created_at")]
    public DateTime CreatedAt { get; init; }

    [JsonProperty("reason")]
    public string Reason { get; init; } = string.Empty;

    [JsonProperty("server_version")]
    public string ServerVersion { get; init; } = string.Empty;

    [JsonProperty("size")]
    public long Size { get; init; }

    [JsonProperty("databases")]
    public List<BackupDatabaseDto> Databases { get; init; } = [];

    [JsonProperty("config_file_count")]
    public int ConfigFileCount { get; init; }

    public static BackupSetDto From(BackupSetSummary summary) =>
        new()
        {
            Id = summary.Id,
            CreatedAt = summary.CreatedAt,
            Reason = summary.Reason,
            ServerVersion = summary.ServerVersion,
            Size = summary.SizeBytes,
            Databases = summary
                .Databases.Select(database => new BackupDatabaseDto
                {
                    Name = database.Name,
                    Size = database.Size,
                    LastMigration = database.LastMigration,
                    Damaged = database.Damaged,
                    Damage = database.Damage,
                })
                .ToList(),
            ConfigFileCount = summary.ConfigFileCount,
        };
}
