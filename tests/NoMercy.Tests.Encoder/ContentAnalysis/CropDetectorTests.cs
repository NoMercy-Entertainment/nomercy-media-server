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

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.ContentAnalysis;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Tests.Encoder.Storage;

namespace NoMercy.Tests.Encoder.ContentAnalysis;

public class CropDetectorTests
{
    private readonly Mock<IProcessRunner> _processRunner = new();
    private readonly EncoderOptions _options = new()
    {
        FfmpegPathOverride = "ffmpeg",
        FfprobePathOverride = "ffprobe",
    };

    [Fact]
    public async Task Detect_StableCrop_ReturnsWithShouldCropTrue()
    {
        string[] stderrLines =
        [
            "frame=1 fps=0 q=-0 size=N/A time=00:00:00.00 bitrate=N/A",
            "[Parsed_cropdetect_0 @ 0x7] x1:0 x2:1919 y1:20 y2:1059 w:1920 h:1040 x:0 y:20 crop=1920:1040:0:20",
            "[Parsed_cropdetect_0 @ 0x7] x1:0 x2:1919 y1:20 y2:1059 w:1920 h:1040 x:0 y:20 crop=1920:1040:0:20",
            "[Parsed_cropdetect_0 @ 0x7] x1:0 x2:1919 y1:20 y2:1059 w:1920 h:1040 x:0 y:20 crop=1920:1040:0:20",
            "[Parsed_cropdetect_0 @ 0x7] x1:0 x2:1919 y1:20 y2:1059 w:1920 h:1040 x:0 y:20 crop=1920:1040:0:20",
            "[Parsed_cropdetect_0 @ 0x7] x1:0 x2:1919 y1:20 y2:1059 w:1920 h:1040 x:0 y:20 crop=1920:1040:0:20",
        ];

        SetupStderr(stderrLines, exitCode: 0);
        CropDetector detector = new(
            _options,
            _processRunner.Object,
            TestStorageFactory.CreateLocal(),
            NullLogger<CropDetector>.Instance
        );

        CropResult result = await detector.DetectAsync("/tmp/in.mkv", CancellationToken.None);

        Assert.Equal(1920, result.Width);
        Assert.Equal(1040, result.Height);
        Assert.Equal(0, result.X);
        Assert.Equal(20, result.Y);
        Assert.True(result.ShouldCrop);
    }

    [Fact]
    public async Task Detect_FullFrameCrop_ShouldCropFalse()
    {
        string[] stderrLines = [.. Enumerable.Repeat("[cropdetect] crop=1920:1080:0:0", 10)];

        SetupStderr(stderrLines, exitCode: 0);
        CropDetector detector = new(
            _options,
            _processRunner.Object,
            TestStorageFactory.CreateLocal(),
            NullLogger<CropDetector>.Instance
        );

        CropResult result = await detector.DetectAsync("/tmp/in.mkv", CancellationToken.None);

        Assert.False(result.ShouldCrop);
    }

    [Fact]
    public async Task Detect_FewerThanMinObservations_ShouldCropFalse()
    {
        string[] stderrLines =
        [
            .. Enumerable.Repeat("crop=1920:1040:0:20", 3), // below threshold
        ];

        SetupStderr(stderrLines, exitCode: 0);
        CropDetector detector = new(
            _options,
            _processRunner.Object,
            TestStorageFactory.CreateLocal(),
            NullLogger<CropDetector>.Instance
        );

        CropResult result = await detector.DetectAsync("/tmp/in.mkv", CancellationToken.None);

        Assert.False(result.ShouldCrop);
    }

    [Fact]
    public async Task Detect_FfmpegNonZeroExit_ReturnsEmptyResult()
    {
        SetupStderr(["crop=1920:1040:0:20"], exitCode: 1);
        CropDetector detector = new(
            _options,
            _processRunner.Object,
            TestStorageFactory.CreateLocal(),
            NullLogger<CropDetector>.Instance
        );

        CropResult result = await detector.DetectAsync("/tmp/in.mkv", CancellationToken.None);

        Assert.Equal(0, result.Width);
        Assert.Equal(0, result.Height);
        Assert.False(result.ShouldCrop);
    }

    [Fact]
    public async Task Detect_PicksMostFrequentCrop()
    {
        // 6 x wide-crop, 2 x no-crop → wide wins
        string[] stderrLines =
        [
            "crop=1920:1040:0:20",
            "crop=1920:1040:0:20",
            "crop=1920:1040:0:20",
            "crop=1920:1080:0:0",
            "crop=1920:1040:0:20",
            "crop=1920:1040:0:20",
            "crop=1920:1080:0:0",
            "crop=1920:1040:0:20",
        ];

        SetupStderr(stderrLines, exitCode: 0);
        CropDetector detector = new(
            _options,
            _processRunner.Object,
            TestStorageFactory.CreateLocal(),
            NullLogger<CropDetector>.Instance
        );

        CropResult result = await detector.DetectAsync("/tmp/in.mkv", CancellationToken.None);

        Assert.Equal(1040, result.Height);
        Assert.Equal(20, result.Y);
        Assert.True(result.ShouldCrop);
    }

    [Fact]
    public async Task Detect_UsesSpecCompliantSampleWindow()
    {
        // Phase 4.1 spec: -ss 60, -t 180, cropdetect round=4.
        string[]? capturedArgs = null;
        _processRunner
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
                    string[] args,
                    Action<string>? _,
                    Action<string>? _,
                    string? _,
                    CancellationToken _
                ) =>
                {
                    capturedArgs = args;
                    return Task.FromResult(
                        new ProcessResult(0, string.Empty, string.Empty, TimeSpan.Zero)
                    );
                }
            );

        CropDetector detector = new(
            _options,
            _processRunner.Object,
            TestStorageFactory.CreateLocal(),
            NullLogger<CropDetector>.Instance
        );

        await detector.DetectAsync("/tmp/in.mkv", CancellationToken.None);

        Assert.NotNull(capturedArgs);
        // -ss 60
        int ssIdx = Array.IndexOf(capturedArgs, "-ss");
        Assert.True(ssIdx >= 0);
        Assert.Equal("60", capturedArgs[ssIdx + 1]);
        // -t 180
        int tIdx = Array.IndexOf(capturedArgs, "-t");
        Assert.True(tIdx >= 0);
        Assert.Equal("180", capturedArgs[tIdx + 1]);
        // cropdetect round=4
        Assert.Contains(capturedArgs, a => a.Contains("round=4"));
    }

    [Fact]
    public async Task Detect_PopulatesSampleFramesAnalyzed_AndConfidence()
    {
        // 6 x wide-crop, 2 x no-crop → confidence = 6/8 = 0.75, frames = 6.
        string[] stderrLines =
        [
            "crop=1920:1040:0:20",
            "crop=1920:1040:0:20",
            "crop=1920:1040:0:20",
            "crop=1920:1080:0:0",
            "crop=1920:1040:0:20",
            "crop=1920:1040:0:20",
            "crop=1920:1080:0:0",
            "crop=1920:1040:0:20",
        ];

        SetupStderr(stderrLines, exitCode: 0);
        CropDetector detector = new(
            _options,
            _processRunner.Object,
            TestStorageFactory.CreateLocal(),
            NullLogger<CropDetector>.Instance
        );

        Guid videoFileId = Guid.NewGuid();
        CropResult result = await detector.DetectAsync(
            "/tmp/in.mkv",
            videoFileId,
            CancellationToken.None
        );

        Assert.Equal(videoFileId, result.SourceVideoFileId);
        Assert.Equal(6, result.SampleFramesAnalyzed);
        Assert.Equal(6.0 / 8.0, result.Confidence, 3);
        Assert.True(result.ShouldCrop);
    }

    [Fact]
    public async Task Detect_BelowMinObservations_StillReportsFramesAndConfidence()
    {
        // 3 observations of one crop → below the MinObservations gate but
        // SampleFramesAnalyzed + Confidence should still be filled in for UI.
        string[] stderrLines = [.. Enumerable.Repeat("crop=1920:1040:0:20", 3)];

        SetupStderr(stderrLines, exitCode: 0);
        CropDetector detector = new(
            _options,
            _processRunner.Object,
            TestStorageFactory.CreateLocal(),
            NullLogger<CropDetector>.Instance
        );

        CropResult result = await detector.DetectAsync(
            "/tmp/in.mkv",
            sourceVideoFileId: null,
            CancellationToken.None
        );

        Assert.False(result.ShouldCrop);
        Assert.Equal(3, result.SampleFramesAnalyzed);
        Assert.Equal(1.0, result.Confidence);
    }

    [Fact]
    public async Task Detect_HdrSource_UsesHdrCropLimit()
    {
        // HDR/PQ black bars sit far above the SDR limit=24, so an HDR source
        // MUST raise the cropdetect limit or the letterbox is never detected
        // and gets baked into a stream-copy. Regression guard for the exact
        // failure seen live (cropdetect → 3840x2160:0:0 on an HDR10 source).
        StrongBox<string[]?> args = CaptureCropDetectArgs();
        CropDetector detector = NewDetector();

        await detector.DetectAsync(
            "/tmp/in.mkv",
            sourceVideoFileId: null,
            sourceIsHdr: true,
            CancellationToken.None
        );

        Assert.NotNull(args.Value);
        Assert.Contains(args.Value!, a => a.Contains("cropdetect") && a.Contains("limit=128"));
        Assert.DoesNotContain(args.Value!, a => a.Contains("limit=24"));
    }

    [Fact]
    public async Task Detect_CapsTheDecoder_NotJustTheOutput()
    {
        // A cropdetect scan is a decode: the whole cost is upstream of the
        // filter. An output -threads reaches the muxer's encoder and leaves the
        // decoder on every core the host has — measured at 10.6 on the sprite
        // pass that carried the same mistake.
        StrongBox<string[]?> args = CaptureCropDetectArgs();
        CropDetector detector = NewDetector();

        await detector.DetectAsync(
            "/tmp/in.mkv",
            sourceVideoFileId: null,
            sourceIsHdr: false,
            CancellationToken.None
        );

        Assert.NotNull(args.Value);
        int threadsAt = Array.IndexOf(args.Value!, "-threads");
        int inputAt = Array.IndexOf(args.Value!, "-i");

        Assert.True(threadsAt >= 0, "the scan must carry a thread cap at all");
        Assert.True(
            threadsAt < inputAt,
            $"-threads sits at {threadsAt}, after -i at {inputAt}, so it never reaches the decoder"
        );
    }

    [Fact]
    public async Task Detect_SdrSource_UsesSdrCropLimit()
    {
        StrongBox<string[]?> args = CaptureCropDetectArgs();
        CropDetector detector = NewDetector();

        await detector.DetectAsync(
            "/tmp/in.mkv",
            sourceVideoFileId: null,
            sourceIsHdr: false,
            CancellationToken.None
        );

        Assert.NotNull(args.Value);
        Assert.Contains(args.Value!, a => a.Contains("cropdetect") && a.Contains("limit=24"));
        Assert.DoesNotContain(args.Value!, a => a.Contains("limit=128"));
    }

    [Fact]
    public async Task Detect_NullHdrFlag_ProbesPqTransfer_UsesHdrLimit()
    {
        // Caller doesn't know the transfer (content-analysis API): the detector
        // probes color_transfer itself. A PQ transfer must select the HDR limit.
        SetupTransferProbe("smpte2084");
        StrongBox<string[]?> args = CaptureCropDetectArgs();
        CropDetector detector = NewDetector();

        await detector.DetectAsync(
            "/tmp/in.mkv",
            sourceVideoFileId: null,
            sourceIsHdr: null,
            CancellationToken.None
        );

        Assert.NotNull(args.Value);
        Assert.Contains(args.Value!, a => a.Contains("cropdetect") && a.Contains("limit=128"));
    }

    [Fact]
    public async Task Detect_NullHdrFlag_ProbesSdrTransfer_UsesSdrLimit()
    {
        SetupTransferProbe("bt709");
        StrongBox<string[]?> args = CaptureCropDetectArgs();
        CropDetector detector = NewDetector();

        await detector.DetectAsync(
            "/tmp/in.mkv",
            sourceVideoFileId: null,
            sourceIsHdr: null,
            CancellationToken.None
        );

        Assert.NotNull(args.Value);
        Assert.Contains(args.Value!, a => a.Contains("cropdetect") && a.Contains("limit=24"));
    }

    [Fact]
    public async Task Detect_SameSourceAcrossBundles_ScansOnce_UntilTheFileChanges()
    {
        // Every bundle of one encode re-ran the 3-minute cropdetect scan over the
        // network share. A shared cache keyed on path, size and modified time
        // makes it run once per source.
        string path = Path.Combine(Path.GetTempPath(), $"crop-cache-{Guid.NewGuid():N}.mkv");
        await File.WriteAllBytesAsync(path, new byte[2048]);
        try
        {
            SetupStderr([.. Enumerable.Repeat("crop=1920:1040:0:20", 10)], exitCode: 0);
            CropResultCache cache = new();

            CropDetector Make() =>
                new(
                    _options,
                    _processRunner.Object,
                    TestStorageFactory.CreateLocal(),
                    NullLogger<CropDetector>.Instance,
                    resultCache: cache
                );

            CropResult first = await Make().DetectAsync(path, CancellationToken.None);
            CropResult second = await Make().DetectAsync(path, CancellationToken.None);

            second.Should().BeEquivalentTo(first);
            first.ShouldCrop.Should().BeTrue();
            ScanCount().Should().Be(1, "the second bundle reuses the first scan");

            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-30));
            await Make().DetectAsync(path, CancellationToken.None);

            ScanCount().Should().Be(2, "a changed file is scanned again");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private int ScanCount() =>
        _processRunner.Invocations.Count(i =>
            i.Method.Name == "RunAsync" && i.Arguments[1] is string[] args && args.Contains("-vf")
        );

    private CropDetector NewDetector() =>
        new(
            _options,
            _processRunner.Object,
            TestStorageFactory.CreateLocal(),
            NullLogger<CropDetector>.Instance
        );

    private void SetupTransferProbe(string transfer) =>
        _processRunner
            .Setup(r =>
                r.RunAsync(
                    "ffprobe",
                    It.IsAny<string[]>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new ProcessResult(0, transfer + "\n", string.Empty, TimeSpan.Zero));

    /// <summary>
    /// Wires the cropdetect (stderr) process call to capture its argument array
    /// into the returned box, filled when the call fires.
    /// </summary>
    private StrongBox<string[]?> CaptureCropDetectArgs()
    {
        StrongBox<string[]?> box = new(null);
        _processRunner
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
                    string[] args,
                    Action<string>? _,
                    Action<string>? _,
                    string? _,
                    CancellationToken _
                ) =>
                {
                    box.Value = args;
                    return Task.FromResult(
                        new ProcessResult(0, string.Empty, string.Empty, TimeSpan.Zero)
                    );
                }
            );
        return box;
    }

    private void SetupStderr(string[] lines, int exitCode)
    {
        _processRunner
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
                    string[] _,
                    Action<string>? onStdOut,
                    Action<string>? onStdErr,
                    string? _,
                    CancellationToken _
                ) =>
                {
                    foreach (string line in lines)
                        onStdErr?.Invoke(line);

                    return Task.FromResult(
                        new ProcessResult(
                            exitCode,
                            string.Empty,
                            string.Join('\n', lines),
                            TimeSpan.FromSeconds(1)
                        )
                    );
                }
            );
    }
}
