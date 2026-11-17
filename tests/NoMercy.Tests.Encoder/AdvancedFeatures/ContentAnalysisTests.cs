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

using NoMercy.Encoder.ContentAnalysis;

namespace NoMercy.Tests.Encoder.AdvancedFeatures;

public class ContentAnalysisTests
{
    [Fact]
    public void CropResult_NoCrop_HasShouldCropFalse()
    {
        CropResult result = new(Width: 1920, Height: 1080, X: 0, Y: 0, ShouldCrop: false);

        result.Width.Should().Be(1920);
        result.Height.Should().Be(1080);
        result.X.Should().Be(0);
        result.Y.Should().Be(0);
        result.ShouldCrop.Should().BeFalse();
    }

    [Fact]
    public void CropResult_WithLetterbox_HasShouldCropTrue()
    {
        CropResult result = new(Width: 1920, Height: 800, X: 0, Y: 140, ShouldCrop: true);

        result.ShouldCrop.Should().BeTrue();
        result.Y.Should().Be(140);
        result.Height.Should().Be(800);
    }

    [Fact]
    public void CropResult_WithPillarbox_HasShouldCropTrue()
    {
        CropResult result = new(Width: 1440, Height: 1080, X: 240, Y: 0, ShouldCrop: true);

        result.ShouldCrop.Should().BeTrue();
        result.X.Should().Be(240);
        result.Width.Should().Be(1440);
    }

    [Fact]
    public void CropResult_ZeroOffset_IsValid()
    {
        CropResult result = new(Width: 0, Height: 0, X: 0, Y: 0, ShouldCrop: false);

        result.Width.Should().Be(0);
        result.Height.Should().Be(0);
        result.ShouldCrop.Should().BeFalse();
    }
}
