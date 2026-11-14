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
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Storage;

namespace NoMercy.Tests.Encoder.Analysis;

/// <summary>
/// The truncation check must compare against the video stream that was
/// encoded, not the container, which ends with the longest stream (#647).
/// </summary>
public class MediaAnalyzerVideoDurationTests
{
    private static EncoderOptions Options() =>
        new() { FfmpegPathOverride = "ffmpeg", FfprobePathOverride = "ffprobe" };

    private static IStorage LocalStorage()
    {
        Mock<IStorage> storage = new();
        storage
            .Setup(s => s.AcquireLocalPath(It.IsAny<string>()))
            .Returns<string>(path => new LocalPathLease(path));
        return storage.Object;
    }

    private static string Probe(string videoExtra) =>
        """
            {
              "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "hevc", "width": 3840, "height": 2160
            """
        + videoExtra
        + """
                },
                { "index": 1, "codec_type": "audio", "codec_name": "eac3" },
                { "index": 2, "codec_type": "subtitle", "codec_name": "subrip", "duration": "3188.000000" }
              ],
              "format": { "duration": "3188.000000" }
            }
            """;

    [Fact]
    public void StreamDuration_WinsOverContainerDuration()
    {
        MediaInfo info = MediaAnalyzer.ParseFfprobeJson(
            Probe(", \"duration\": \"3117.000000\""),
            "/media/episode.mkv"
        );

        info.Duration.TotalSeconds.Should().Be(3188);
        info.VideoDuration.Should().Be(TimeSpan.FromSeconds(3117));
        info.EncodedVideoDuration.TotalSeconds.Should().Be(3117);
    }

    [Fact]
    public void TagDuration_IsUsedWhenTheStreamHasNone()
    {
        MediaInfo info = MediaAnalyzer.ParseFfprobeJson(
            Probe(", \"tags\": { \"DURATION-eng\": \"00:51:57.000000000\" }"),
            "/media/episode.mkv"
        );

        info.VideoDuration.Should().Be(TimeSpan.FromSeconds(3117));
    }

    [Fact]
    public void NoVideoDuration_FallsBackToTheContainer()
    {
        MediaInfo info = MediaAnalyzer.ParseFfprobeJson(Probe(string.Empty), "/media/episode.mkv");

        info.VideoDuration.Should().BeNull();
        info.EncodedVideoDuration.TotalSeconds.Should().Be(3188);
    }

    [Fact]
    public void LastVideoPacket_IsParsedAsTheStreamEnd()
    {
        const string packets = """
            {
              "packets": [
                { "pts_time": "3116.822000", "duration_time": "0.041000" },
                { "pts_time": "3116.905000", "duration_time": "0.041000" }
              ]
            }
            """;

        MediaAnalyzer.ParseLastVideoPacketEnd(packets).Should().BeApproximately(3116.946, 0.001);
        MediaAnalyzer.ParseLastVideoPacketEnd("{\"packets\":[]}").Should().BeNull();
    }

    [Fact]
    public async Task AnalyzeAsync_ProbesTheVideoTail_WhenStreamAndTagAreAbsent()
    {
        const string packets = """
            {"packets":[{"pts_time":"3116.905000","duration_time":"0.041000"}]}
            """;
        List<string[]> calls = [];
        Mock<IProcessRunner> runner = new();
        runner
            .Setup(r =>
                r.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<string, string[], string?, CancellationToken>(
                (_, args, _, _) =>
                {
                    calls.Add(args);
                    string stdOut = calls.Count == 1 ? Probe(string.Empty) : packets;
                    return Task.FromResult(
                        new ProcessResult(0, stdOut, string.Empty, TimeSpan.Zero)
                    );
                }
            );
        MediaAnalyzer analyzer = new(runner.Object, LocalStorage(), Options());

        MediaInfo info = await analyzer.AnalyzeAsync("/media/episode.mkv");

        calls.Should().HaveCount(2);
        calls[1].Should().Contain("-read_intervals");
        info.VideoDuration!.Value.TotalSeconds.Should().BeApproximately(3116.946, 0.001);
        info.Duration.TotalSeconds.Should().Be(3188);
    }

    [Fact]
    public async Task AnalyzeAsync_SkipsTheTailProbe_WhenTheStreamCarriesItsDuration()
    {
        Mock<IProcessRunner> runner = new();
        runner
            .Setup(r =>
                r.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                new ProcessResult(
                    0,
                    Probe(", \"duration\": \"3117.000000\""),
                    string.Empty,
                    TimeSpan.Zero
                )
            );
        MediaAnalyzer analyzer = new(runner.Object, LocalStorage(), Options());

        MediaInfo info = await analyzer.AnalyzeAsync("/media/episode.mkv");

        runner.Verify(
            r =>
                r.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
        info.VideoDuration.Should().Be(TimeSpan.FromSeconds(3117));
    }
}
