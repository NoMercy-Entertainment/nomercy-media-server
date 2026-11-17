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

using FluentAssertions;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Profiles;

namespace NoMercy.Tests.Encoder.Profiles;

[Trait("Category", "Unit")]
public sealed class CodecTuningTests
{
    // x265 "slower" ran a 1080p library preset at 1 fps on a CPU without AVX2.
    // Ultra is one step below Archive on every codec, x265 included.
    [Fact]
    public void H265_Ultra_IsOneStepFasterThanArchive()
    {
        CodecTunings.For(VideoCodecType.H265, EncodingQuality.Archive).Preset.Should().Be("slower");
        CodecTunings.For(VideoCodecType.H265, EncodingQuality.Ultra).Preset.Should().Be("slow");
        CodecTunings.For(VideoCodecType.H265, EncodingQuality.Ultra).Crf.Should().Be(18);
    }
}
