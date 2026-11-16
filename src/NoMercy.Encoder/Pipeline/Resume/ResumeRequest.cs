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

using NoMercy.Encoder.Commands;
using NoMercy.Encoder.Output;

namespace NoMercy.Encoder.Pipeline.Resume;

/// <param name="Commands">The commands built for a start at zero; their arguments name the segment files and their hash is the marker.</param>
public record ResumeRequest(
    OutputPlan Plan,
    FfmpegCommand[] Commands,
    string OutputDirectory,
    EncodingPass Pass,
    TimeSpan? DurationLimit,
    long? ResumeFromMs,
    string CorrelationId
);
