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

namespace NoMercy.MediaProcessing.AudioAnalysis;

/// <summary>
/// What one analysis pass measured. Every member is nullable because a partial
/// result is the normal case — the detectors are independent, and one of them
/// finding nothing does not invalidate the others.
/// </summary>
public sealed record AudioAnalysisResult
{
    public double? Bpm { get; init; }
    public double? BpmConfidence { get; init; }
    public int? BeatOffsetMs { get; init; }
    public double? BeatIntervalMs { get; init; }

    /// <summary>As the detector named it: "C", "F#", "Am".</summary>
    public string? KeyName { get; init; }
    public double? KeyConfidence { get; init; }

    public double? IntegratedLufs { get; init; }
    public double? TruePeakDb { get; init; }
    public double? LoudnessRange { get; init; }
    public double? SpectralCentroid { get; init; }

    public int? IntroEndMs { get; init; }
    public int? OutroStartMs { get; init; }
}
