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

/// <summary>
/// Enough to draw a progress indicator for audio analysis: what is left, what
/// succeeded, what gave up, and whether the queue is running at all.
/// </summary>
public record AudioAnalysisStatusDto
{
    [JsonProperty("paused")]
    public bool Paused { get; set; }

    /// <summary>Analysis jobs still waiting on the music queue.</summary>
    [JsonProperty("queued")]
    public int Queued { get; set; }

    [JsonProperty("analyzed")]
    public int Analyzed { get; set; }

    /// <summary>
    /// Tracks that will not be retried until the analyzer version changes.
    /// Shown separately so a stalled-looking count is explainable.
    /// </summary>
    [JsonProperty("failed")]
    public int Failed { get; set; }
}
