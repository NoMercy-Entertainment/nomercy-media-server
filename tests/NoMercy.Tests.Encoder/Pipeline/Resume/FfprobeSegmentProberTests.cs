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

using Moq;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Encoder.Pipeline.Resume;

namespace NoMercy.Tests.Encoder.Pipeline.Resume;

public class FfprobeSegmentProberTests
{
    [Theory]
    [InlineData("9.408000\n", 9.408)]
    [InlineData("\n11.410000,\n12.0\n", 11.41)]
    public void ParseFirstPts_ReadsTheFirstPacket(string output, double expected) =>
        FfprobeSegmentProber.ParseFirstPts(output).Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData("N/A\n")]
    public void ParseFirstPts_NoNumber_ReturnsNull(string output) =>
        FfprobeSegmentProber.ParseFirstPts(output).Should().BeNull();

    [Fact]
    public async Task FirstPacketPtsAsync_ProbesTheFirstPacketOfTheVideoStream()
    {
        string[] captured = [];
        Mock<IProcessRunner> runner = new();
        runner
            .Setup(r =>
                r.RunAsync(
                    "ffprobe",
                    It.IsAny<string[]>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<Action<string>?>(),
                    It.IsAny<Action<string>?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<Action<int>?>()
                )
            )
            .Callback<
                string,
                string[],
                TimeSpan,
                Action<string>?,
                Action<string>?,
                string?,
                CancellationToken,
                Action<int>?
            >((_, args, _, _, _, _, _, _) => captured = args)
            .ReturnsAsync(new ProcessResult(0, "9.408000\n", "", TimeSpan.Zero));
        FfprobeSegmentProber prober = new(runner.Object, new() { FfprobePathOverride = "ffprobe" });

        double? pts = await prober.FirstPacketPtsAsync("video/v_00004.ts", true, default);

        pts.Should().Be(9.408);
        captured.Should().Contain(["-select_streams", "v:0", "-show_entries", "packet=pts_time"]);
        captured.Should().EndWith("video/v_00004.ts");
    }

    [Fact]
    public async Task FirstPacketPtsAsync_ProbeExitsNonZero_ReturnsNull()
    {
        Mock<IProcessRunner> runner = new();
        runner
            .Setup(r =>
                r.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<Action<string>?>(),
                    It.IsAny<Action<string>?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<Action<int>?>()
                )
            )
            .ReturnsAsync(new ProcessResult(1, "", "bad file", TimeSpan.Zero));
        FfprobeSegmentProber prober = new(runner.Object, new() { FfprobePathOverride = "ffprobe" });

        (await prober.FirstPacketPtsAsync("x.ts", false, default)).Should().BeNull();
    }

    [Fact]
    public async Task FirstPtsAsync_ProbesWithoutPickingAStream()
    {
        string[] captured = [];
        Mock<IProcessRunner> runner = new();
        runner
            .Setup(r =>
                r.RunAsync(
                    "ffprobe",
                    It.IsAny<string[]>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<Action<string>?>(),
                    It.IsAny<Action<string>?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<Action<int>?>()
                )
            )
            .Callback<
                string,
                string[],
                TimeSpan,
                Action<string>?,
                Action<string>?,
                string?,
                CancellationToken,
                Action<int>?
            >((_, args, _, _, _, _, _, _) => captured = args)
            .ReturnsAsync(new ProcessResult(0, "1.400000\n", "", TimeSpan.Zero));
        FfprobeSegmentProber prober = new(runner.Object, new() { FfprobePathOverride = "ffprobe" });

        double? pts = await prober.FirstPtsAsync("audio/a_00000.ts", default);

        pts.Should().Be(1.4);
        captured.Should().NotContain("-select_streams");
        captured.Should().Contain(["-show_entries", "packet=pts_time"]);
    }
}
