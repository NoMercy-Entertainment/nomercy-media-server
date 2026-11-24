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

using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.ContentAnalysis.Segments;
using NoMercy.Encoder.ContentAnalysis.Segments.Probes;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Encoder.Subtitles;
using NoMercy.NmSystem.Monitoring;
using NoMercy.Storage;
using NoMercy.Tests.Encoder.Storage;

namespace NoMercy.Tests.Encoder.ContentAnalysis.Segments;

/// <summary>
/// The FFmpeg command lines of the segment analysis and how their output is
/// read back. FFmpeg itself is not run: the runner plays it, writing the
/// output files and stderr lines a real run produces.
/// </summary>
[Trait("Category", "Unit")]
public class FfmpegEpisodeSignalProbeTests
{
    private readonly IStorage _storage = TestStorageFactory.CreateLocal();
    private readonly SegmentMediaSource _source;

    public FfmpegEpisodeSignalProbeTests()
    {
        _source = new(
            _storage,
            "/media/show/S01E01/audio_jpn.m3u8",
            1,
            "/media/show/S01E01/video.m3u8"
        );
    }

    [Fact]
    public async Task ReadAudio_FingerprintsTheWholeTrack_AsRaw_AndReadsTheBinaryBack()
    {
        string[]? args = null;
        Mock<IProcessRunner> runner = Runner(
            (a, stderr) =>
            {
                args = a;
                byte[] raw = new byte[12];
                BinaryPrimitives.WriteUInt32LittleEndian(raw, 3665379362u);
                BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), 3660120098u);
                BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(8), 61u); // '=' then zeros
                File.WriteAllBytes(a[^1], raw);
                stderr("  Duration: 00:23:00.21, start: 1.400000, bitrate: 0 kb/s");
                stderr(
                    "[Parsed_silencedetect_1 @ 0x1] silence_end: 3.024875 | silence_duration: 1.582552"
                );
                stderr("[Parsed_silencedetect_1 @ 0x1] silence_start: 1377.201333");
            }
        );

        AudioSignals? audio = await Probe(runner).ReadAudioAsync(_source, CancellationToken.None);

        audio.Should().NotBeNull();
        audio!.DurationSeconds.Should().BeApproximately(1380.21, 1e-9);
        audio.Hashes.Should().Equal(3665379362u, 3660120098u, 61u);
        audio.Silences.Should().HaveCount(2);
        audio.Silences[^1].End.Should().Be(1380.21);

        // #344: no window. The whole track, from its first sample.
        args.Should().NotContain(["-ss", "-sseof", "-t"]);
        args.Should().ContainInConsecutiveOrder("-f", "chromaprint", "-fp_format", "raw");
        string.Join(' ', args!)
            .Should()
            .Contain("[0:a:1]asplit[a1][a2];[a1]silencedetect=n=-50dB:d=0.3[ao]");
    }

    [Fact]
    public async Task ReadAudio_OfASingleFile_PicksTheStreamByLanguage_AndFallsBackToItsIndex()
    {
        List<string> graphs = [];
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
            .Returns<
                string,
                string[],
                TimeSpan,
                Action<string>?,
                Action<string>?,
                string?,
                CancellationToken,
                Action<int>?
            >(
                (_, args, _, _, stderr, _, _, _) =>
                {
                    string graph = args[Array.IndexOf(args, "-filter_complex") + 1];
                    graphs.Add(graph);
                    if (graph.Contains("language"))
                    {
                        stderr?.Invoke(
                            "[fc#0 @ 0x1] Stream specifier ':a:m:language:jpn' in filtergraph description ... matches no streams."
                        );
                        return Task.FromResult(
                            new ProcessResult(1, "", "matches no streams", TimeSpan.Zero)
                        );
                    }
                    File.WriteAllBytes(args[^1], new byte[8]);
                    stderr?.Invoke("  Duration: 00:42:00.00, start: 0.000000, bitrate: 0 kb/s");
                    return Task.FromResult(new ProcessResult(0, "", "", TimeSpan.Zero));
                }
            );
        SegmentMediaSource file = new(_storage, "/media/movie.mkv", 2, "/media/movie.mkv", "jpn");

        AudioSignals? audio = await Probe(runner).ReadAudioAsync(file, CancellationToken.None);

        audio.Should().NotBeNull();
        graphs.Should().HaveCount(2);
        graphs[0].Should().StartWith("[0:a:m:language:jpn]");
        graphs[1].Should().StartWith("[0:a:2]");
    }

    [Fact]
    public async Task ReadAudio_OfABrokenFile_CostsOnePass_NotTwo()
    {
        int passes = 0;
        Mock<IProcessRunner> runner = Runner(
            (_, stderr) =>
            {
                passes++;
                stderr(
                    "[in#0 @ 0x1] Error opening input: Invalid data found when processing input"
                );
            },
            exitCode: 1
        );
        SegmentMediaSource file = new(_storage, "/media/broken.mkv", 0, "/media/broken.mkv", "jpn");

        (await Probe(runner).ReadAudioAsync(file, CancellationToken.None)).Should().BeNull();
        passes.Should().Be(1, "only a language that matched no stream is retried by index");
    }

    [Theory]
    [InlineData("jpn", "jpn")]
    [InlineData("en", "en")]
    [InlineData("j]n;x", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void LanguageTags_InTheFiltergraph_AreLettersOnly(string? language, string? tag)
    {
        FfmpegEpisodeSignalProbe.LanguageTag(language).Should().Be(tag);
    }

    [Fact]
    public async Task ReadAudio_FailedRun_IsNull()
    {
        Mock<IProcessRunner> runner = Runner((_, _) => { }, exitCode: 1);

        (await Probe(runner).ReadAudioAsync(_source, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ReadAudio_WhenFfmpegCannotStart_IsNull_NotAnException()
    {
        Mock<IProcessRunner> runner = Runner(
            (_, _) =>
                throw new System.ComponentModel.Win32Exception(
                    2,
                    "The system cannot find the file specified."
                )
        );

        (await Probe(runner).ReadAudioAsync(_source, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ReadBlack_RunsBothDetectorsPerWindow_InEpisodeTime()
    {
        List<string[]> calls = [];
        Mock<IProcessRunner> runner = Runner(
            (a, stderr) =>
            {
                calls.Add(a);
                stderr(
                    "[blackdetect@strict @ 0x1] black_start:3.12 black_end:5.56 black_duration:2.44"
                );
                stderr(
                    "[blackdetect@loose @ 0x2] black_start:3.08 black_end:6.2 black_duration:3.12"
                );
            }
        );

        BlackRuns? black = await Probe(runner)
            .ReadBlackAsync(_source, [new(0, 6.633), new(900.21, 1380.21)], CancellationToken.None);

        calls.Should().HaveCount(2);
        calls[1].Should().ContainInConsecutiveOrder("-ss", "900.21", "-t", "480");
        string.Join(' ', calls[0])
            .Should()
            .Contain("blackdetect@loose=d=1.0:pix_th=0.10:pic_th=0.80");
        black!.Strict.Should().HaveCount(2);
        black.Loose[1].Start.Should().BeApproximately(903.29, 1e-9);
    }

    [Fact]
    public async Task ReadBlack_OnABuildThatDoesNotNameInstances_RunsOnePassPerDetector()
    {
        // FFmpeg 8.0 NoMercy builds before release 1.0.44 log both detectors as
        // "[blackdetect @ ...]" (measured on the installed Jan 2026 build).
        List<string> graphs = [];
        Mock<IProcessRunner> runner = Runner(
            (a, stderr) =>
            {
                string graph = a[Array.IndexOf(a, "-vf") + 1];
                graphs.Add(graph);
                if (graph.Contains("strict") && !graph.Contains("loose"))
                    stderr("[blackdetect @ 0x1] black_start:5 black_end:6 black_duration:1");
                else
                    stderr("[blackdetect @ 0x2] black_start:3 black_end:8.04 black_duration:5.04");
            }
        );

        BlackRuns? black = await Probe(runner)
            .ReadBlackAsync(_source, [new(100, 120)], CancellationToken.None);

        graphs.Should().HaveCount(3);
        black!.Strict.Should().Equal(new TimeRange(105, 106));
        black.Loose.Should().ContainSingle().Which.Start.Should().Be(103);
    }

    [Fact]
    public async Task ProbeEdge_Looks6SecondsEachWay()
    {
        string[]? args = null;
        Mock<IProcessRunner> runner = Runner(
            (a, stderr) =>
            {
                args = a;
                stderr("[scdet @ 0x1] lavfi.scd.score: 34.120, lavfi.scd.time: 5.2");
                stderr("[blackdetect @ 0x2] black_start:0.8 black_end:1.6 black_duration:0.8");
            }
        );

        EdgeProbe? edge = await Probe(runner).ProbeEdgeAsync(_source, 81.1, CancellationToken.None);

        args.Should().ContainInConsecutiveOrder("-ss", "75.1", "-t", "12");
        edge!.Cuts.Should().ContainSingle().Which.Should().BeApproximately(80.3, 1e-9);
        edge.Black.Should().ContainSingle().Which.Start.Should().BeApproximately(75.9, 1e-9);
    }

    [Fact]
    public async Task ReadThumbnails_AreTwoGreyFramesPerSecond_FromTheSpanStart()
    {
        Mock<IProcessRunner> runner = Runner(
            (a, _) => File.WriteAllBytes(a[^1], new byte[2 * SegmentRules.ThumbnailPixels + 10])
        );

        ThumbnailStrip? strip = await Probe(runner)
            .ReadThumbnailsAsync(_source, new(100, 101), CancellationToken.None);

        strip!.Times.Should().Equal(100.0, 100.5);
        strip.Pixels.Should().HaveCount(2 * SegmentRules.ThumbnailPixels);
    }

    [Fact]
    public async Task ReadText_WithoutATesseractModel_IsSkipped()
    {
        Mock<IProcessRunner> runner = Runner((_, _) => { });
        Mock<ITesseractModelManager> tesseract = new();
        tesseract
            .Setup(t => t.EnsureLanguageModelAsync("eng", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no model"));

        OcrScan? ocr = await Probe(runner, tesseract.Object)
            .ReadTextAsync(_source, 960, CancellationToken.None);

        ocr.Should().BeNull();
    }

    [Fact]
    public async Task ReadText_ReadsTheOcrDump_FromTheWorkingDirectory()
    {
        string[]? args = null;
        Mock<IProcessRunner> runner = new();
        runner
            .Setup(r =>
                r.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<IReadOnlyDictionary<string, string>?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<
                string,
                string[],
                IReadOnlyDictionary<string, string>?,
                string?,
                CancellationToken
            >(
                (_, a, _, workingDirectory, _) =>
                {
                    args = a;
                    File.WriteAllText(
                        Path.Combine(workingDirectory!, "ocr.txt"),
                        "frame:0    pts:0       pts_time:0\nlavfi.ocr.text=Directed by Yasunori Urata\n\n"
                    );
                }
            )
            .ReturnsAsync(new ProcessResult(0, "", "", TimeSpan.Zero));
        Mock<ITesseractModelManager> tesseract = new();
        tesseract
            .Setup(t => t.EnsureLanguageModelAsync("eng", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Path.Combine(Path.GetTempPath(), "tessdata", "eng.traineddata"));

        OcrScan? ocr = await Probe(runner, tesseract.Object)
            .ReadTextAsync(_source, 960, CancellationToken.None);

        ocr!.StartSeconds.Should().Be(960);
        ocr.Samples.Should().Equal(new OcrSample(960, 3));
        string.Join(' ', args!)
            .Should()
            .Contain("ocr=language=eng,metadata=print:key=lavfi.ocr.text:file=ocr.txt");
    }

    [Fact]
    public async Task ReadText_WithoutATesseractModel_WarnsWithTheCause()
    {
        Mock<IProcessRunner> runner = Runner((_, _) => { });
        Mock<ITesseractModelManager> tesseract = new();
        tesseract
            .Setup(t => t.EnsureLanguageModelAsync("eng", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no model"));
        CapturingLogger log = new();

        await Probe(runner, tesseract.Object, log)
            .ReadTextAsync(_source, 960, CancellationToken.None);

        log.Entries.Should()
            .Contain(e => e.Level == LogLevel.Warning && e.Exception!.Message == "no model");
    }

    [Fact]
    public async Task ReadText_WhenFfmpegFails_WarnsWithTheStderrTail()
    {
        Mock<IProcessRunner> runner = new();
        runner
            .Setup(r =>
                r.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<IReadOnlyDictionary<string, string>?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new ProcessResult(1, "", "No such filter: 'ocr'", TimeSpan.Zero));
        Mock<ITesseractModelManager> tesseract = new();
        tesseract
            .Setup(t => t.EnsureLanguageModelAsync("eng", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Path.Combine(Path.GetTempPath(), "tessdata", "eng.traineddata"));
        CapturingLogger log = new();

        OcrScan? ocr = await Probe(runner, tesseract.Object, log)
            .ReadTextAsync(_source, 960, CancellationToken.None);

        ocr.Should().BeNull();
        log.Entries.Should()
            .Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("No such filter"));
    }

    private sealed class CapturingLogger : ILogger<FfmpegEpisodeSignalProbe>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Entries.Add((logLevel, formatter(state, exception), exception));
    }

    private FfmpegEpisodeSignalProbe Probe(
        Mock<IProcessRunner> runner,
        ITesseractModelManager? tesseract = null,
        ILogger<FfmpegEpisodeSignalProbe>? logger = null
    ) =>
        new(
            new EncoderOptions
            {
                FfmpegPathOverride = "/usr/bin/ffmpeg",
                FfprobePathOverride = "/usr/bin/ffprobe",
            },
            runner.Object,
            _storage,
            tesseract ?? new Mock<ITesseractModelManager>().Object,
            new MediaActivityMonitor(),
            logger ?? NullLogger<FfmpegEpisodeSignalProbe>.Instance
        );

    private static Mock<IProcessRunner> Runner(
        Action<string[], Action<string>> play,
        int exitCode = 0
    )
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
            .Callback<
                string,
                string[],
                TimeSpan,
                Action<string>?,
                Action<string>?,
                string?,
                CancellationToken,
                Action<int>?
            >((_, args, _, _, stderr, _, _, _) => play(args, line => stderr?.Invoke(line)))
            .ReturnsAsync(new ProcessResult(exitCode, "", "", TimeSpan.Zero));
        return runner;
    }
}
