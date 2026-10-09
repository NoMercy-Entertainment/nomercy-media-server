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

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Encoder.Errors;
using NoMercy.Encoder.Jobs;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Progress;
using NoMercy.Encoder.Strategies.Hls;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Tests.Encoder.Storage;
using Container = NoMercy.Encoder.Profiles.Container;

namespace NoMercy.Tests.Encoder.Strategies;

/// <summary>
/// Verifies destination-side partial output sweep on encode crash (item 3).
///
/// On a non-cancel failure (ffmpeg exits non-zero), the strategy must delete
/// any partial output the current run wrote to the output directory so the
/// directory is not left in a half-written state. The crash checkpoint written
/// by ExecuteStage is intentionally left intact for the resume path.
///
/// A successful encode must NOT touch the output directory.
/// </summary>
public class CrashSweepTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly Mock<IEncoder> _encoder;
    private readonly Mock<ICheckpointStore> _checkpointStore;
    private readonly LocalStorage _storage;
    private readonly HlsTwoPassStrategy _strategy;

    public CrashSweepTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"CrashSweep_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);

        _encoder = new(MockBehavior.Strict);
        _checkpointStore = new();
        _storage = TestStorageFactory.CreateLocal();

        _strategy = new(
            _encoder.Object,
            _checkpointStore.Object,
            NullLogger<HlsTwoPassStrategy>.Instance,
            _storage
        );
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Crash_DeletesPartialOutputFiles()
    {
        string outputDir = Path.Combine(_tempRoot, "output");
        Directory.CreateDirectory(outputDir);

        // Simulate a partial write: one segment file exists before the crash.
        string partialSegment = Path.Combine(outputDir, "video_0.ts");
        await File.WriteAllTextAsync(partialSegment, "partial segment data");

        _encoder
            .Setup(e =>
                e.EncodeAsync(
                    It.IsAny<EncodingRequest>(),
                    It.IsAny<IProgressObserver?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                new EncodingResult(
                    Success: false,
                    OutputPath: string.Empty,
                    Duration: TimeSpan.Zero,
                    Error: new(
                        Kind: EncodingErrorKind.ProcessCrashed,
                        Message: "ffmpeg crashed",
                        FfmpegStderr: null,
                        StageName: "Execute",
                        Recoverable: true
                    ),
                    Metrics: null
                )
            );

        _checkpointStore
            .Setup(s => s.LoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((JobCheckpoint?)null);

        EncodingRequest request = new(
            InputPath: "/media/source.mkv",
            OutputDirectory: outputDir,
            Profile: new(
                Id: Ulid.NewUlid(),
                Name: "Test HLS",
                Container: Container.HlsTs,
                Video: null,
                Audio: [],
                Subtitles: []
            )
        );

        EncodingResult result = await _strategy.EncodeAsync(
            request,
            progress: null,
            ct: CancellationToken.None
        );

        result.Success.Should().BeFalse();
        File.Exists(partialSegment).Should().BeFalse("crash sweep must delete partial segments");
    }

    [Fact]
    public async Task Crash_CheckpointFilePreserved()
    {
        // Crash checkpoint written by ExecuteStage must NOT be deleted by the
        // strategy's partial-sweep — orphan recovery needs it for resume.
        string outputDir = Path.Combine(_tempRoot, "output");
        Directory.CreateDirectory(outputDir);

        string checkpointFile = Path.Combine(outputDir, ".checkpoint.json");
        await File.WriteAllTextAsync(checkpointFile, "{\"JobId\":\"abc\"}");

        string partialSegment = Path.Combine(outputDir, "video_0.ts");
        await File.WriteAllTextAsync(partialSegment, "partial");

        _encoder
            .Setup(e =>
                e.EncodeAsync(
                    It.IsAny<EncodingRequest>(),
                    It.IsAny<IProgressObserver?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                new EncodingResult(
                    Success: false,
                    OutputPath: string.Empty,
                    Duration: TimeSpan.Zero,
                    Error: new(
                        Kind: EncodingErrorKind.ProcessCrashed,
                        Message: "crashed",
                        FfmpegStderr: null,
                        StageName: "Execute",
                        Recoverable: true
                    ),
                    Metrics: null
                )
            );

        _checkpointStore
            .Setup(s => s.LoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((JobCheckpoint?)null);

        EncodingRequest request = new(
            InputPath: "/media/source.mkv",
            OutputDirectory: outputDir,
            Profile: new(
                Id: Ulid.NewUlid(),
                Name: "Test HLS",
                Container: Container.HlsTs,
                Video: null,
                Audio: [],
                Subtitles: []
            )
        );

        await _strategy.EncodeAsync(request, progress: null, ct: CancellationToken.None);

        // Checkpoint survives the sweep — the resume path needs it.
        File.Exists(checkpointFile).Should().BeTrue("crash checkpoint must survive the sweep");
        // The partial segment is swept.
        File.Exists(partialSegment).Should().BeFalse();
    }

    [Fact]
    public async Task Success_DoesNotSweepOutputFiles()
    {
        string outputDir = Path.Combine(_tempRoot, "output");
        Directory.CreateDirectory(outputDir);

        string finalSegment = Path.Combine(outputDir, "video_0.ts");
        await File.WriteAllTextAsync(finalSegment, "real segment data");

        _encoder
            .Setup(e =>
                e.EncodeAsync(
                    It.IsAny<EncodingRequest>(),
                    It.IsAny<IProgressObserver?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                new EncodingResult(
                    Success: true,
                    OutputPath: outputDir,
                    Duration: TimeSpan.FromSeconds(10),
                    Error: null,
                    Metrics: null
                )
            );

        _checkpointStore
            .Setup(s => s.LoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((JobCheckpoint?)null);

        EncodingRequest request = new(
            InputPath: "/media/source.mkv",
            OutputDirectory: outputDir,
            Profile: new(
                Id: Ulid.NewUlid(),
                Name: "Test HLS",
                Container: Container.HlsTs,
                Video: null,
                Audio: [],
                Subtitles: []
            )
        );

        await _strategy.EncodeAsync(request, progress: null, ct: CancellationToken.None);

        File.Exists(finalSegment).Should().BeTrue("success must never delete output files");
    }
}
