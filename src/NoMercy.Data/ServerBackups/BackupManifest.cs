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
/// The <c>manifest.json</c> of a backup set: what is in it, which schema each
/// database is at, and a SHA-256 per file so a restore can prove the set is whole.
/// Every path is relative to the set folder and uses forward slashes.
/// </summary>
public sealed class BackupManifest
{
    public const int CurrentFormatVersion = 1;
    public const string FileName = "manifest.json";

    [JsonProperty("format_version")]
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    [JsonProperty("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonProperty("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonProperty("server_version")]
    public string ServerVersion { get; set; } = string.Empty;

    [JsonProperty("databases")]
    public List<BackupManifestDatabase> Databases { get; set; } = [];

    [JsonProperty("config_files")]
    public List<BackupManifestFile> ConfigFiles { get; set; } = [];
}
