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

using Microsoft.Extensions.Logging;
using NoMercy.Encoder.Composition;

namespace NoMercy.Encoder.Subscribers;

/// <summary>
/// Crop detection is already wired into <c>PlanStage</c> via the per-profile
/// <c>AutoDetectCrop</c> flag — when a profile opts in, the planner runs
/// <c>ICropDetector</c> at plan time and threads the resulting crop filter
/// into every variant's filter chain. This subscriber exists for symmetry
/// with the other Phase 4.2 subscribers and to give ops a single opt-out
/// surface (<see cref="EncoderOptions.EnableCropDetectSubscriber"/>) — when
/// disabled the in-plan crop detection is bypassed.
///
/// The actual detection runs inside PlanStage, not here, because the crop
/// rectangle has to be resolved BEFORE filter chain construction, not
/// afterwards.
/// </summary>
public sealed class CropDetectSubscriber
{
    private readonly EncoderOptions _options;
    private readonly ILogger<CropDetectSubscriber> _logger;

    public CropDetectSubscriber(EncoderOptions options, ILogger<CropDetectSubscriber> logger)
    {
        _options = options;
        _logger = logger;

        if (!_options.EnableCropDetectSubscriber)
        {
            _logger.LogInformation(
                "CropDetectSubscriber disabled — PlanStage will skip auto-detect-crop"
            );
        }
    }

    /// <summary>
    /// Mirrors <see cref="EncoderOptions.EnableCropDetectSubscriber"/>, the
    /// flag <c>PlanStage</c> reads alongside <c>profile.AutoDetectCrop</c> to
    /// decide whether to actually invoke the crop detector. False means the
    /// detector is globally disabled regardless of profile setting.
    /// </summary>
    public bool IsEnabled => _options.EnableCropDetectSubscriber;
}
