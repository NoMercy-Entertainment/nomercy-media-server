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
using Moq;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Infrastructure;
using NoMercy.MediaProcessing.AudioAnalysis;
using NoMercy.Storage;

namespace NoMercy.Tests.MediaProcessing.AudioAnalysis;

/// <summary>
/// The command the analyzer builds, and what it makes of the output. ffmpeg is
/// mocked: the fixture lines are replayed through the same callbacks the real
/// process runner writes to.
/// </summary>
public class FfmpegAudioAnalyzerTests
{
    /// <summary>
    /// Locked deliberately. The order and the single ametadata are
    /// load-bearing: aspectralstats re-frames and, downstream of beatdetect,
    /// dropped the frame carrying <c>final=1</c> on real MP3s; two ametadata
    /// instances printing to the same stdout splice each other's lines; and
    /// the print has to sit ahead of loudnorm, which alters the signal and
    /// re-frames its output.
    /// </summary>
    private const string ExpectedFilterGraph =
        "aspectralstats=measure=centroid,beatdetect,keydetect,"
        + "ametadata=mode=print:file=-,silencedetect=n=-50dB:d=0.5,"
        + "loudnorm=print_format=json";

    private string[] _capturedArguments = [];
    private readonly Mock<ILogger<FfmpegAudioAnalyzer>> _logger = new();

    private FfmpegAudioAnalyzer CreateAnalyzer(string stdOutFixture, string stdErrFixture)
    {
        EncoderOptions options = new() { FfmpegPathOverride = "ffmpeg" };

        Mock<IStorage> storage = new();
        storage
            .Setup(s => s.AcquireLocalPath(It.IsAny<string>()))
            .Returns((string path) => new LocalPathLease(path));

        Mock<IProcessRunner> runner = new();
        runner
            .Setup(r =>
                r.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<Action<string>?>(),
                    It.IsAny<Action<string>?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(
                (
                    string _,
                    string[] arguments,
                    Action<string>? onStdOut,
                    Action<string>? onStdErr,
                    string? _,
                    CancellationToken _
                ) =>
                {
                    _capturedArguments = arguments;

                    foreach (string line in ReadFixture(stdOutFixture))
                    {
                        onStdOut?.Invoke(line);
                    }

                    foreach (string line in ReadFixture(stdErrFixture))
                    {
                        onStdErr?.Invoke(line);
                    }

                    return Task.FromResult(
                        new ProcessResult(0, string.Empty, string.Empty, default)
                    );
                }
            );

        return new(options, runner.Object, storage.Object, _logger.Object);
    }

    /// <summary>
    /// Counts warnings whose rendered message contains <paramref name="fragment" />.
    /// The message template is the third Log argument; matching on it rather
    /// than on the exact call shape survives reformatting of the log statement.
    /// </summary>
    private int WarningsMentioning(string fragment)
    {
        return _logger.Invocations.Count(invocation =>
            invocation.Method.Name == nameof(ILogger.Log)
            && invocation.Arguments[0] is LogLevel.Warning
            && invocation.Arguments[2]?.ToString()?.Contains(fragment, StringComparison.Ordinal)
                == true
        );
    }

    private static string[] ReadFixture(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "AudioAnalysis", "Fixtures", name);
        return File.ReadAllLines(path);
    }

    /// <summary>
    /// The version stamped on every stored row. Bumping it re-queues the library,
    /// so it must move with every change to what the pass measures.
    /// </summary>
    [Fact]
    public void TheAnalyzerVersionMatchesTheMetadataBeatGrid()
    {
        CreateAnalyzer("v1040-click-128-stdout.txt", "v1040-click-128-stderr.txt")
            .Version.Should()
            .Be(4);
    }

    [Fact]
    public async Task ItRunsOneDetectorPassAndPrintsMetadataRightAfterBeatdetect()
    {
        FfmpegAudioAnalyzer analyzer = CreateAnalyzer(
            "v1040-click-128-stdout.txt",
            "v1040-click-128-stderr.txt"
        );

        await analyzer.AnalyzeAsync("/music/track.flac", CancellationToken.None);

        int filterIndex = Array.IndexOf(_capturedArguments, "-af");

        filterIndex.Should().BeGreaterThan(-1);
        _capturedArguments[filterIndex + 1].Should().Be(ExpectedFilterGraph);
    }

    [Fact]
    public async Task ItReadsTheBeatGridFromTheMetadataTheGraphPrints()
    {
        FfmpegAudioAnalyzer analyzer = CreateAnalyzer(
            "v1040-click-128-stdout.txt",
            "v1040-click-128-stderr.txt"
        );

        AudioAnalysisResult? result = await analyzer.AnalyzeAsync(
            "/music/track.flac",
            CancellationToken.None
        );

        result.Should().NotBeNull();
        result!.BeatGridFromMetadata.Should().BeTrue();
        result.Bpm.Should().BeApproximately(128.0, 0.5);
        result.BeatOffsetMs.Should().NotBeNull();
        result.KeyName.Should().Be("C");
    }

    [Fact]
    public async Task ItSaysNothingAboutTheFallbackWhenTheGridWasMeasured()
    {
        FfmpegAudioAnalyzer analyzer = CreateAnalyzer(
            "v1040-click-128-stdout.txt",
            "v1040-click-128-stderr.txt"
        );

        await analyzer.AnalyzeAsync("/music/track.flac", CancellationToken.None);

        WarningsMentioning("legacy stderr tempo").Should().Be(0);
    }

    /// <summary>
    /// A server whose ffmpeg publishes no beat metadata still measures a tempo,
    /// so its rows look merely unconfident rather than unmeasurable. The warning
    /// is the only place that distinction is visible.
    /// </summary>
    [Fact]
    public async Task ItWarnsWhenTheTempoCameFromTheLegacyStderrLine()
    {
        FfmpegAudioAnalyzer analyzer = CreateAnalyzer(
            "click-100bpm-stdout.txt",
            "click-100bpm-stderr.txt"
        );

        AudioAnalysisResult? result = await analyzer.AnalyzeAsync(
            "/music/track.flac",
            CancellationToken.None
        );

        result.Should().NotBeNull();
        result!.BeatGridFromMetadata.Should().BeFalse();
        result.Bpm.Should().NotBeNull();
        WarningsMentioning("legacy stderr tempo").Should().Be(1);
    }
}
