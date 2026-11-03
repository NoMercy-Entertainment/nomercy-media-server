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
using NoMercy.Encoder.Distribution;
using NoMercy.Encoder.Execution;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Encoder.Progress;

namespace NoMercy.Tests.Encoder.Distribution;

/// <summary>
/// The coordinator dispatch endpoint never sets a time range, so every
/// dispatched task used to run with a zero input duration and report 0 %
/// for its whole life. These pin the fallback: time range, then the
/// declared source duration, then a probe of the input path.
/// </summary>
public class LocalWorkerDispatcherInputDurationTests
{
    private static readonly TimeSpan SourceDuration = TimeSpan.FromMinutes(10);

    [Fact]
    public async Task DispatchAsync_ReportsHalfProgress_WhenNoTimeRangeAndSourceDurationKnown()
    {
        // Real executor over a fake ffmpeg that reports out_time at half the source.
        Mock<IProcessRunner> runner = new();
        runner
            .Setup(r =>
                r.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<Action<string>?>(),
                    It.IsAny<Action<string>?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<Action<int>?>()
                )
            )
            .Callback<
                string,
                string[],
                Action<string>?,
                Action<string>?,
                string?,
                CancellationToken,
                CancellationToken,
                Action<int>?
            >(
                (_, _, onStdOut, _, _, _, _, _) =>
                {
                    onStdOut?.Invoke("frame=100");
                    onStdOut?.Invoke("fps=30.0");
                    onStdOut?.Invoke(
                        $"out_time_us={(long)(SourceDuration.TotalSeconds / 2 * 1_000_000)}"
                    );
                    onStdOut?.Invoke("speed=2.0x");
                    onStdOut?.Invoke("progress=continue");
                }
            )
            .ReturnsAsync(new ProcessResult(0, "", "", TimeSpan.FromSeconds(1)));

        FfmpegExecutor executor = new(runner.Object, NullLogger<FfmpegExecutor>.Instance);
        RecordingSink sink = new();
        LocalWorkerDispatcher dispatcher = new(
            executor,
            sink,
            NullLogger<LocalWorkerDispatcher>.Instance
        );

        EncodeTask task = MakeTask() with { SourceDuration = SourceDuration };

        await dispatcher.DispatchAsync([task], CancellationToken.None);

        sink.Reports.Should().NotBeEmpty();
        sink.Reports[^1]
            .PercentComplete.Should()
            .BeApproximately(
                50,
                0.5,
                "a task without a time range must report progress against the source duration"
            );
        sink.Reports[^1].EstimatedRemaining.Should().NotBeNull();
    }

    [Fact]
    public async Task DispatchAsync_ProbesTheInput_WhenTaskDeclaresNoDuration()
    {
        Mock<ISourceDurationProbe> probe = new();
        probe
            .Setup(p => p.ProbeAsync("/media/source.mkv", It.IsAny<CancellationToken>()))
            .ReturnsAsync(SourceDuration);
        (Mock<IFfmpegExecutor> executor, List<TimeSpan> seenDurations) = RecordingExecutor();

        LocalWorkerDispatcher dispatcher = new(
            executor.Object,
            new NullTaskProgressSink(),
            probe.Object,
            NullLogger<LocalWorkerDispatcher>.Instance
        );

        EncodeTask task = MakeTask() with { InputPath = "/media/source.mkv" };

        await dispatcher.DispatchAsync([task], CancellationToken.None);

        seenDurations
            .Should()
            .Equal([SourceDuration], "the executor must receive the probed source duration");
    }

    [Fact]
    public async Task DispatchAsync_PrefersTheTimeRange_OverTheSourceDuration()
    {
        Mock<ISourceDurationProbe> probe = new();
        (Mock<IFfmpegExecutor> executor, List<TimeSpan> seenDurations) = RecordingExecutor();

        LocalWorkerDispatcher dispatcher = new(
            executor.Object,
            new NullTaskProgressSink(),
            probe.Object,
            NullLogger<LocalWorkerDispatcher>.Instance
        );

        TimeSpan range = TimeSpan.FromMinutes(2);
        EncodeTask task = MakeTask() with
        {
            InputPath = "/media/source.mkv",
            SourceDuration = SourceDuration,
            TimeRangeDuration = range,
        };

        await dispatcher.DispatchAsync([task], CancellationToken.None);

        seenDurations.Should().Equal([range]);
        probe.Verify(
            p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task DispatchAsync_StillRunsTheTask_WhenTheProbeCannotMeasureTheSource()
    {
        Mock<ISourceDurationProbe> probe = new();
        probe
            .Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TimeSpan?)null);
        (Mock<IFfmpegExecutor> executor, List<TimeSpan> seenDurations) = RecordingExecutor();

        LocalWorkerDispatcher dispatcher = new(
            executor.Object,
            new NullTaskProgressSink(),
            probe.Object,
            NullLogger<LocalWorkerDispatcher>.Instance
        );

        EncodeTask task = MakeTask() with { InputPath = "/media/unreadable.mkv" };

        DispatchResult[] results = await dispatcher.DispatchAsync([task], CancellationToken.None);

        results.Should().ContainSingle().Which.Success.Should().BeTrue();
        seenDurations.Should().Equal([TimeSpan.Zero]);
    }

    private static (Mock<IFfmpegExecutor>, List<TimeSpan>) RecordingExecutor()
    {
        List<TimeSpan> seen = [];
        Mock<IFfmpegExecutor> executor = new();
        executor
            .Setup(e =>
                e.ExecuteAsync(
                    It.IsAny<FfmpegCommand>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<Action<EncodingProgress>>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<
                FfmpegCommand,
                TimeSpan,
                Action<EncodingProgress>?,
                string?,
                CancellationToken
            >((_, duration, _, _, _) => seen.Add(duration))
            .ReturnsAsync(
                new ExecutionResult(
                    Success: true,
                    ExitCode: 0,
                    StdErr: "",
                    Duration: TimeSpan.FromSeconds(1),
                    Error: null
                )
            );
        return (executor, seen);
    }

    private static EncodeTask MakeTask() =>
        new(
            TaskId: "task-0",
            Command: new("ffmpeg", ["-i", "in.mkv", "-progress", "pipe:1", "out.ts"], null),
            OutputPath: "/out/a.m3u8",
            Type: EncodeTaskType.QualityVariant
        );

    private sealed class RecordingSink : ITaskProgressSink
    {
        public List<EncodingProgress> Reports { get; } = [];

        public void Report(string taskId, EncodingProgress progress) => Reports.Add(progress);
    }
}
