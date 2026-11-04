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
using Newtonsoft.Json.Linq;

namespace NoMercy.Encoder.Bundle;

/// <summary>
/// The source's mux assembly spec — see spec Part 2. <see cref="Ffprobe"/> is
/// stored with its original JSON values, and every <see cref="BlueprintTrack"/> joins into it by
/// <c>source_stream_index</c>.
/// </summary>
public record BlueprintSource(
    /// <summary>The original source path, never a staging lease.</summary>
    [property: JsonProperty("path")] string Path,
    [property: JsonProperty("filename")] string Filename,
    [property: JsonProperty("container")] string Container,
    [property: JsonProperty("size_bytes")] long SizeBytes,
    [property: JsonProperty("duration_seconds")] double DurationSeconds,
    // Deferred: computing this requires a full re-read of the source file. No
    // streaming hasher is wired into the analyzer pipeline yet — see spec
    // "Open items".
    [property: JsonProperty("sha256")] string? Sha256,
    // format + streams + chapters as ffprobe emitted them. Every track in
    // every encodes[] entry joins into this by source_stream_index.
    [property: JsonProperty("ffprobe")] JObject? Ffprobe
);
