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

using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Subscribers;

namespace NoMercy.Tests.Encoder.Subscribers;

/// <summary>
/// CropDetectSubscriber is an opt-out toggle — <see cref="CropDetectSubscriber.IsEnabled"/>
/// mirrors <see cref="EncoderOptions.EnableCropDetectSubscriber"/>, which PlanStage
/// reads alongside the per-profile AutoDetectCrop flag to decide whether to
/// actually invoke ICropDetector (see PlanStageCropTests). False here means the
/// detector is globally disabled regardless of profile.
/// </summary>
public class CropDetectSubscriberTests
{
    [Fact]
    public void IsEnabled_DefaultsToTrueFromOptions()
    {
        EncoderOptions options = new();
        // Sanity: encoder ships with crop detect on by default.
        options.EnableCropDetectSubscriber.Should().BeTrue();

        CropDetectSubscriber subject = new(options, NullLogger<CropDetectSubscriber>.Instance);

        subject.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void IsEnabled_FollowsOptionsToggle()
    {
        EncoderOptions options = new() { EnableCropDetectSubscriber = false };

        CropDetectSubscriber subject = new(options, NullLogger<CropDetectSubscriber>.Instance);

        subject.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void Constructor_DoesNotThrow_WhenDisabled()
    {
        // Logging on construction when disabled is a side-effect we don't
        // want crashing the host. Verify construction never throws either way.
        EncoderOptions options = new() { EnableCropDetectSubscriber = false };

        Action act = () =>
            _ = new CropDetectSubscriber(options, NullLogger<CropDetectSubscriber>.Instance);

        act.Should().NotThrow();
    }
}
