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

[Obsolete("Replaced by V2EncodingProfile body on PUT /{id:ulid}. Kept for reference only.")]
public record UpdateEncoderProfileRequest(
    [property: JsonProperty("name")] string? Name = null,
    [property: JsonProperty("description")] string? Description = null,
    [property: JsonProperty("profile_json")] string? ProfileJson = null
);
