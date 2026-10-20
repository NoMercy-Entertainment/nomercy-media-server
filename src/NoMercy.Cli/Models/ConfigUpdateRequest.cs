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

namespace NoMercy.Cli.Models;

/// <summary>
/// Body of <c>PUT /manage/config</c>, mirrored key for key from the server's
/// <c>ManagementConfigUpdateDto</c>. Every property is nullable so one request can
/// carry a single key and leave the rest untouched. The ports and the Swagger flag
/// are absent on purpose: the server reports them but never reads them back.
/// </summary>
internal class ConfigUpdateRequest
{
    [JsonProperty("server_name")]
    public string? ServerName { get; set; }

    [JsonProperty("library_workers")]
    public int? LibraryWorkers { get; set; }

    [JsonProperty("import_workers")]
    public int? ImportWorkers { get; set; }

    [JsonProperty("extras_workers")]
    public int? ExtrasWorkers { get; set; }

    [JsonProperty("encoder_workers")]
    public int? EncoderWorkers { get; set; }

    [JsonProperty("cron_workers")]
    public int? CronWorkers { get; set; }

    [JsonProperty("image_workers")]
    public int? ImageWorkers { get; set; }

    [JsonProperty("file_workers")]
    public int? FileWorkers { get; set; }

    [JsonProperty("music_workers")]
    public int? MusicWorkers { get; set; }
}
