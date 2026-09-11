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

namespace NoMercy.Api.DTOs.Music;

/// <summary>One chord change, as ffmpeg's keydetect filter timed it.</summary>
public record ChordDto
{
    [JsonProperty("ms")]
    public int Ms { get; set; }

    /// <summary>As the detector named it, for example "Am" or "F#maj".</summary>
    [JsonProperty("chord")]
    public string Chord { get; set; } = string.Empty;
}
