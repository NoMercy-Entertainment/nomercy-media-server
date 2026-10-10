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

using System.Text;
using NoMercy.Encoder.Execution;

namespace NoMercy.Tests.Encoder.Execution;

[Trait("Category", "Unit")]
public class FfmpegStderrTests
{
    [Fact]
    public void Tail_BoundsACorruptFilesDecoderFlood()
    {
        StringBuilder flood = new();
        for (int i = 0; i < 5000; i++)
            flood.AppendLine($"[h264 @ 0000] Invalid NAL unit size ({i} > 100).");
        flood.AppendLine("Conversion failed!");

        string tail = FfmpegStderr.Tail(flood.ToString());

        Assert.True(tail.Length <= 500);
        // The fatal reason is the last thing ffmpeg prints, so it must survive.
        Assert.Contains("Conversion failed!", tail);
    }

    [Fact]
    public void Tail_LeavesAShortStderrIntact()
    {
        const string stderr = "Unknown encoder 'h264_qsv'";

        Assert.Equal(stderr, FfmpegStderr.Tail(stderr));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Tail_ReportsEmptyRatherThanNothing(string? stderr)
    {
        Assert.Equal("<empty>", FfmpegStderr.Tail(stderr!));
    }
}
