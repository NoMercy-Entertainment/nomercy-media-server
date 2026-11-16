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

/// <summary>One file of a backup set, with the size and checksum it was written with.</summary>
public class BackupManifestFile
{
    /// <summary>Relative to the set folder, forward slashes: <c>config/seeds/libraries.jsonc</c>.</summary>
    [JsonProperty("path")]
    public string Path { get; set; } = string.Empty;

    [JsonProperty("size")]
    public long Size { get; set; }

    /// <summary>Lowercase hex SHA-256 of the file.</summary>
    [JsonProperty("sha256")]
    public string Sha256 { get; set; } = string.Empty;
}
