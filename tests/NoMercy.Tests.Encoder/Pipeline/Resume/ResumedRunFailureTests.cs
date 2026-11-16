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
using NoMercy.Encoder.Commands;
using NoMercy.Encoder.Errors;
using NoMercy.Encoder.Execution;
using NoMercy.Encoder.Jobs;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Pipeline.Resume;
using NoMercy.Encoder.Pipeline.Stages;
using NoMercy.Encoder.Progress;
using NoMercy.Tests.Encoder.Storage;

namespace NoMercy.Tests.Encoder.Pipeline.Resume;

/// <summary>
/// A resumed run that exits non-zero drops its marker, so the next attempt
/// starts at zero instead of resuming onto segments the failed run touched.
/// </summary>
public class ResumedRunFailureTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        $"resume_exec_{Guid.NewGuid():N}"
    );
    private readonly string _markerPath;

    public ResumedRunFailureTests()
    {
        Directory.CreateDirectory(_dir);
        _markerPath = Path.Combine(_dir, ".resume.abc");
        File.WriteAllText(_markerPath, "run-0");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task ResumedRunExitsNonZero_DeletesTheMarker()
    {
        StageResult result = await Run(success: false, markerPath: _markerPath);

        result.Should().BeOfType<StageFailure>();
        File.Exists(_markerPath).Should().BeFalse();
    }

    [Fact]
    public async Task UnresumedRunExitsNonZero_KeepsTheMarker()
    {
        await Run(success: false, markerPath: null);

        File.Exists(_markerPath).Should().BeTrue();
    }

    [Fact]
    public async Task ResumedRunSucceeds_KeepsTheMarker()
    {
        StageResult result = await Run(success: true, markerPath: _markerPath);

        result.Should().BeOfType<StageSuccess<ExecutionResult[]>>();
        File.Exists(_markerPath).Should().BeTrue();
    }

    private async Task<StageResult> Run(bool success, string? markerPath)
    {
        Mock<IFfmpegExecutor> executor = new();
        executor
            .Setup(e =>
                e.ExecuteAsync(
                    It.IsAny<FfmpegCommand>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<Action<EncodingProgress>?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                success
                    ? new ExecutionResult(true, 0, "", TimeSpan.Zero, null)
                    : new ExecutionResult(
                        false,
                        1,
                        "seek failed",
                        TimeSpan.Zero,
                        new(EncodingErrorKind.Unknown, "exec failed", "seek failed", "exec", false)
                    )
            );
        ExecuteStage stage = new(
            executor.Object,
            new Mock<ICheckpointStore>().Object,
            NullLogger<ExecuteStage>.Instance,
            new EncodeResumeMarker(TestStorageFactory.CreateLocal())
        );
        FfmpegCommand command = new("ffmpeg", ["-i", "in.mkv"], null, markerPath);

        return await stage.ExecuteAsync(
            new([command], TimeSpan.FromMinutes(1)),
            new("ctx-1", OutputDirectory: _dir, InputPath: "/media/src.mkv"),
            CancellationToken.None
        );
    }
}
