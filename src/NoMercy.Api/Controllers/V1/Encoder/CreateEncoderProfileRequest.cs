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

namespace NoMercy.Api.Controllers.V1.Encoder;

public record CreateEncoderProfileRequest(
    [property: JsonProperty("name")] string Name,
    [property: JsonProperty("profile_json")] string ProfileJson,
    [property: JsonProperty("description")] string? Description = null,
    [property: JsonProperty("author")] string? Author = null,
    [property: JsonProperty("tags")] string? Tags = null,
    [property: JsonProperty("parent_preset_id")] Ulid? ParentPresetId = null
);
