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

using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.Pipeline.Optimizer;
using NoMercy.Tests.Encoder.Hardware;

namespace NoMercy.Tests.Encoder.Pipeline.Optimizer;

public class EncodeCostModelTests
{
    private static readonly SpeedIndex EmptySpeed = new([]);

    [Fact]
    public void RungCost_HigherResolution_CostsMore()
    {
        EncodeCostModel model = new(new StubSpeedIndexProvider(EmptySpeed));

        double uhd = model.RungCost(3840, 2160, VideoCodecType.H265, "libx265", passes: 1);
        double fhd = model.RungCost(1920, 1080, VideoCodecType.H265, "libx265", passes: 1);

        uhd.Should().BeGreaterThan(fhd, "more pixels = more encode work");
    }

    [Fact]
    public void RungCost_TwoPass_DoublesSinglePass()
    {
        EncodeCostModel model = new(new StubSpeedIndexProvider(EmptySpeed));

        double single = model.RungCost(1920, 1080, VideoCodecType.H264, "libx264", passes: 1);
        double twoPass = model.RungCost(1920, 1080, VideoCodecType.H264, "libx264", passes: 2);

        twoPass.Should().BeApproximately(single * 2, 0.001);
    }

    [Fact]
    public void RungCost_AfterBenchmarkPublishes_UsesNewSpeed()
    {
        // Issue #350: the model must price with the index the benchmark holds
        // now, not the one it saw when it was built.
        StubSpeedIndexProvider provider = new(EmptySpeed);
        EncodeCostModel model = new(provider);
        double before = model.RungCost(1920, 1080, VideoCodecType.H265, "hevc_nvenc", passes: 1);

        provider.Current = new(
            new()
            {
                [new(VideoCodecType.H265, "hevc_nvenc", 1920, null)] = new(
                    Fps: 120,
                    SpeedMultiplier: 4.0,
                    MeasuredAt: default
                ),
            }
        );
        double after = model.RungCost(1920, 1080, VideoCodecType.H265, "hevc_nvenc", passes: 1);

        after
            .Should()
            .BeApproximately(before / 4.0, 0.001, "a 4x realtime encoder costs a quarter");
    }

    [Fact]
    public void RungCost_FasterEncoder_CostsLess()
    {
        // A benchmarked encoder with 4x realtime is cheaper than the 1.0 default.
        SpeedIndex fast = new(
            new()
            {
                [new(VideoCodecType.H265, "hevc_nvenc", 1920, null)] = new(
                    Fps: 120,
                    SpeedMultiplier: 4.0,
                    MeasuredAt: default
                ),
            }
        );
        EncodeCostModel hw = new(new StubSpeedIndexProvider(fast));
        EncodeCostModel sw = new(new StubSpeedIndexProvider(EmptySpeed));

        double hwCost = hw.RungCost(1920, 1080, VideoCodecType.H265, "hevc_nvenc", passes: 1);
        double swCost = sw.RungCost(1920, 1080, VideoCodecType.H265, "libx265", passes: 1);

        hwCost.Should().BeLessThan(swCost, "a 4x-realtime encoder costs less wall-time");
    }

    [Fact]
    public void TotalCost_SumsRungsPlusDecodeAndTonemap()
    {
        EncodeCostModel model = new(new StubSpeedIndexProvider(EmptySpeed));

        double withTonemap = model.TotalCost(
            sourceWidth: 3840,
            sourceHeight: 2160,
            sourceIsHdr: true,
            rungs:
            [
                new(1920, 1080, VideoCodecType.H265, "libx265", 1),
                new(1280, 720, VideoCodecType.H265, "libx265", 1),
            ]
        );
        double withoutTonemap = model.TotalCost(
            sourceWidth: 3840,
            sourceHeight: 2160,
            sourceIsHdr: false,
            rungs:
            [
                new(1920, 1080, VideoCodecType.H265, "libx265", 1),
                new(1280, 720, VideoCodecType.H265, "libx265", 1),
            ]
        );

        withTonemap.Should().BeGreaterThan(withoutTonemap, "HDR adds a tonemap pass");
    }
}
