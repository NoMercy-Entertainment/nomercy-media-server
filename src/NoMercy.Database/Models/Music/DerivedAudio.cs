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

using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace NoMercy.Database.Models.Music;

/// <summary>
/// The register of the derived-audio store: one row per content hash. The
/// file lives at <c>cache/derived/&lt;key[0..2]&gt;/&lt;key&gt;</c>; the row
/// carries what eviction needs (size, last use) so the policy is a query,
/// not a directory walk.
/// </summary>
[PrimaryKey(nameof(Key))]
public class DerivedAudio
{
    /// <summary>Lower-case hex sha256 of the content.</summary>
    [MaxLength(64)]
    [JsonProperty("key")]
    public string Key { get; set; } = string.Empty;

    [MaxLength(32)]
    [JsonProperty("content_type")]
    public string ContentType { get; set; } = string.Empty;

    [JsonProperty("bytes")]
    public long Bytes { get; set; }

    [JsonProperty("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonProperty("last_used_at")]
    public DateTime LastUsedAt { get; set; }
}
