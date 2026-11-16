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

namespace NoMercy.Encoder.Pipeline.Resume;

/// <summary>
/// Where an interrupted HLS encode continues: first segment and source position; zero to start over.
/// </summary>
public record ResumeDecision(
    bool Resume,
    int StartSegment,
    long ResumeMs,
    string Reason,
    string? MarkerPath = null,
    long TimestampDelayMs = 0
)
{
    public static ResumeDecision StartAtZero(string reason) => new(false, 0, 0, reason);
}
