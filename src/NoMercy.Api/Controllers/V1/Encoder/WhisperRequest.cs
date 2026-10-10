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

public record WhisperRequest(
    [property: JsonProperty("audio_stream_index")] int AudioStreamIndex,
    [property: JsonProperty("language")] string Language,
    [property: JsonProperty("translate_to_english")] bool TranslateToEnglish,
    [property: JsonProperty("model")] string? Model
);
