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
using NoMercy.Database.Models.Media;

namespace NoMercy.Api.Controllers.V1.Media;

public record UpdateContentSegmentRequest(
    [property: JsonProperty("segment_type")] ContentSegmentType? SegmentType = null,
    [property: JsonProperty("start_seconds")] double? StartSeconds = null,
    [property: JsonProperty("end_seconds")] double? EndSeconds = null,
    [property: JsonProperty("confidence")] double? Confidence = null
);
