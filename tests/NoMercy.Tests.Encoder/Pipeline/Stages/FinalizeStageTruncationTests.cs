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

using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.BuildingBlocks;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Execution;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Pipeline.Stages;
using NoMercy.Encoder.Profiles;
using NoMercy.Tests.Encoder.Bundle;

namespace NoMercy.Tests.Encoder.Pipeline.Stages;

/// <summary>
/// #647: a container that ends after the video (audio or subtitles run on)
/// is not a truncated encode; a video stream longer than the output still is.
/// </summary>
public sealed class FinalizeStageTruncationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "nomercy-finalize-647-" + Guid.NewGuid().ToString("N")
    );

    public FinalizeStageTruncationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
    }

    private void WritePlaylist(string folder, double totalSeconds)
    {
        string dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        List<string> lines = ["#EXTM3U", "#EXT-X-TARGETDURATION:10"];
        int segment = 0;
        for (double left = totalSeconds; left > 0; left -= 10)
        {
            double length = Math.Min(10, left);
            lines.Add("#EXTINF:" + length.ToString("F3", CultureInfo.InvariantCulture) + ",");
            lines.Add($"seg{segment}.ts");
            File.WriteAllText(Path.Combine(dir, $"seg{segment}.ts"), "x");
            segment++;
        }
        lines.Add("#EXT-X-ENDLIST");
        File.WriteAllLines(Path.Combine(dir, folder + ".m3u8"), lines);
    }

    private static FinalizeStage BuildStage()
    {
        Mock<IOutputStrategy> strategy = new();
        strategy
            .Setup(s =>
                s.FinalizeAsync(
                    It.IsAny<string>(),
                    It.IsAny<OutputPlan>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(Task.CompletedTask);
        strategy.Setup(s => s.Format).Returns(OutputFormat.Hls);
        Mock<IOutputStrategyFactory> factory = new();
        factory.Setup(f => f.Resolve(It.IsAny<OutputFormat>())).Returns(strategy.Object);
        Mock<IFontExtractor> fonts = new();
        fonts
            .Setup(f => f.WriteFontManifestAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        return new(
            new Mock<IChapterWriter>().Object,
            fonts.Object,
            factory.Object,
            new EncoderOptions { FfmpegPathOverride = "ffmpeg", FfprobePathOverride = "ffprobe" },
            new Mock<IProcessRunner>().Object,
            NullLogger<FinalizeStage>.Instance,
            new TestStorage()
        );
    }

    private FinalizeInput Input() =>
        new(
            Results:
            [
                new(
                    Success: true,
                    ExitCode: 0,
                    StdErr: string.Empty,
                    Duration: TimeSpan.Zero,
                    Error: null
                ),
            ],
            Plan: new(
                Format: OutputFormat.Hls,
                VideoOutputs: [],
                AudioOutputs: [],
                SubtitleOutputs: [],
                Thumbnails: null
            ),
            OutputDirectory: _root,
            MediaTitle: "Episode",
            HlsDerivatives: new() { GenerateChapters = false, GenerateFontsJson = false }
        );

    private static EncodingContext Context(double containerSeconds, double videoSeconds) =>
        EncodingContext.Create() with
        {
            MediaInfo = new(
                FilePath: "/src/episode.mkv",
                Format: "matroska",
                Duration: TimeSpan.FromSeconds(containerSeconds),
                OverallBitRateKbps: 8000,
                FileSizeBytes: 1,
                VideoStreams: [],
                AudioStreams: [],
                SubtitleStreams: [],
                Chapters: [],
                VideoDuration: TimeSpan.FromSeconds(videoSeconds)
            ),
        };

    [Fact]
    public async Task ContainerLongerThanTheVideoStream_Passes()
    {
        WritePlaylist("video_1920x1080", 3117);
        WritePlaylist("video_3840x2160", 3117);

        StageResult result = await BuildStage()
            .ExecuteAsync(Input(), Context(3188, 3117), CancellationToken.None);

        result.Should().BeOfType<StageSuccess<FinalizeOutput>>();
    }

    [Fact]
    public async Task OutputShorterThanTheVideoStream_StillFails()
    {
        WritePlaylist("video_1920x1080", 3117);

        StageResult result = await BuildStage()
            .ExecuteAsync(Input(), Context(3188, 3188), CancellationToken.None);

        StageFailure failure = result.Should().BeOfType<StageFailure>().Subject;
        failure.Error.Message.Should().Contain("truncated");
        failure.Error.Message.Should().Contain("3117.0 s");
    }
}
