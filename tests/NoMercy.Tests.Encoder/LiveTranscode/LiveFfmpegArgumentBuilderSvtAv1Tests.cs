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
using NoMercy.Encoder.LiveTranscode;

namespace NoMercy.Tests.Encoder.LiveTranscode;

/// <summary>
/// libsvtav1 refuses a max bitrate outside CRF mode, so a live AV1 run with -b:v plus -maxrate exits with code 234.
/// </summary>
public class LiveFfmpegArgumentBuilderSvtAv1Tests
{
    private static LiveRunInput Input(VideoCodecType codec, string encoder) =>
        new(
            InputPath: "/media/in.mkv",
            OutputDirectory: "/tmp/live",
            StartPosition: TimeSpan.Zero,
            Quality: new LiveQuality(
                Id: "1080p",
                Label: "1080p",
                Width: 1920,
                Height: 1080,
                Codec: codec,
                BitrateKbps: 6000,
                Encoder: encoder,
                IsHardwareAccelerated: false,
                ExpectedSpeed: 1.0,
                CanRealtime: true
            ),
            SegmentDurationSeconds: 6
        );

    [Fact]
    public void SvtAv1_GetsTheAbrTargetWithoutAMaxBitrate()
    {
        string[] args = LiveFfmpegArgumentBuilder.Build(Input(VideoCodecType.Av1, "libsvtav1"));

        args.Should().ContainInOrder("-c:v", "libsvtav1");
        args.Should().ContainInOrder("-b:v", "6000k");
        args.Should().NotContain("-maxrate");
        args.Should().NotContain("-bufsize");
    }

    [Fact]
    public void X264_KeepsItsVbvCap()
    {
        string[] args = LiveFfmpegArgumentBuilder.Build(Input(VideoCodecType.H264, "libx264"));

        args.Should().ContainInOrder("-maxrate", "9000k");
        args.Should().ContainInOrder("-bufsize", "12000k");
    }
}
