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
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Encoder.Commands;
using NoMercy.Encoder.Execution;
using NoMercy.Encoder.Infrastructure;
using NoMercy.NmSystem.Monitoring;

namespace NoMercy.Tests.Encoder.Execution;

/// <summary>
/// A pause from the dashboard must hold a running encode until the operator
/// resumes it. The live-playback monitor in <see cref="FfmpegExecutor"/> shares
/// the throttle and must never wake an encode the operator paused.
/// </summary>
public class OperatorPauseTests
{
    private const int ProcessId = 4242;
    private const int JobId = 1001;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    private readonly Mock<IProcessRunner> _processRunner = new();
    private readonly Mock<IProcessSuspender> _suspender = new();
    private readonly TaskCompletionSource<ProcessResult> _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    private ProcessThrottle BuildThrottle(ILogger<ProcessThrottle>? logger = null) =>
        new(logger ?? NullLogger<ProcessThrottle>.Instance, _suspender.Object);

    private Task<ExecutionResult> StartEncode(ProcessThrottle throttle, Func<bool> needsCapacity)
    {
        _processRunner
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
            .Returns(
                (
                    string _,
                    string[] _,
                    Action<string>? _,
                    Action<string>? _,
                    string? _,
                    CancellationToken _,
                    CancellationToken _,
                    Action<int>? onStarted
                ) =>
                {
                    onStarted?.Invoke(ProcessId);
                    return _completion.Task;
                }
            );

        FfmpegExecutor executor = new(
            _processRunner.Object,
            NullLogger<FfmpegExecutor>.Instance,
            needsCapacity,
            new MediaActivityMonitor(),
            throttle,
            PollInterval
        );

        return executor.ExecuteAsync(
            new FfmpegCommand("ffmpeg", ["-i", "/input.mkv", "/output.mp4"], null),
            TimeSpan.FromMinutes(1)
        );
    }

    private async Task FinishEncode(Task<ExecutionResult> encode)
    {
        _completion.SetResult(new ProcessResult(0, "", "", TimeSpan.FromSeconds(1)));
        (await encode).Success.Should().BeTrue();
    }

    [Fact]
    public async Task PauseQueue_SuspendsRunningEncode_WhileNoPlaybackNeedsCapacity()
    {
        ProcessThrottle throttle = BuildThrottle();
        Task<ExecutionResult> encode = StartEncode(throttle, () => false);

        throttle.Suspend(ProcessId);

        SpinWait
            .SpinUntil(() => !throttle.IsSuspended(ProcessId), TimeSpan.FromMilliseconds(300))
            .Should()
            .BeFalse("the playback monitor must not wake an encode the operator paused");
        _suspender.Verify(s => s.Resume(ProcessId), Times.Never);

        await FinishEncode(encode);
    }

    [Fact]
    public async Task ResumeQueue_ResumesSuspendedEncode()
    {
        ProcessThrottle throttle = BuildThrottle();
        Task<ExecutionResult> encode = StartEncode(throttle, () => false);
        throttle.Suspend(ProcessId);
        await Task.Delay(PollInterval * 10);
        throttle.IsSuspended(ProcessId).Should().BeTrue();

        throttle.Resume(ProcessId);

        throttle.IsSuspended(ProcessId).Should().BeFalse();
        _suspender.Verify(s => s.Resume(ProcessId), Times.Once);

        await FinishEncode(encode);
    }

    [Fact]
    public async Task PauseQueue_DuringPlaybackYield_StaysSuspendedWhenPlaybackEnds()
    {
        ProcessThrottle throttle = BuildThrottle();
        using CancellationTokenSource playbackEnded = new();
        Task<ExecutionResult> encode = StartEncode(
            throttle,
            () => !playbackEnded.IsCancellationRequested
        );
        SpinWait
            .SpinUntil(() => throttle.IsSuspended(ProcessId), TimeSpan.FromSeconds(1))
            .Should()
            .BeTrue();

        throttle.Suspend(ProcessId);
        await playbackEnded.CancelAsync();
        await Task.Delay(PollInterval * 10);

        throttle.IsSuspended(ProcessId).Should().BeTrue();
        _suspender.Verify(s => s.Suspend(ProcessId), Times.Once);
        _suspender.Verify(s => s.Resume(ProcessId), Times.Never);

        await FinishEncode(encode);
    }

    [Fact]
    public async Task ExitedEncode_LeavesNoStaleHold_SoAReusedPidIsSuspendedAgain()
    {
        ProcessThrottle throttle = BuildThrottle();
        Task<ExecutionResult> encode = StartEncode(throttle, () => false);
        throttle.Suspend(ProcessId);

        await FinishEncode(encode);

        throttle.IsSuspended(ProcessId).Should().BeFalse();
        throttle.Suspend(ProcessId);
        _suspender.Verify(s => s.Suspend(ProcessId), Times.Exactly(2));
    }

    [Fact]
    public void PauseQueue_SuspendsEveryRegisteredEncode()
    {
        ProcessThrottle throttle = BuildThrottle();
        EncoderProcessRegistry registry = new();
        registry.Register(JobId, ProcessId);
        registry.Register(JobId + 1, ProcessId + 1);

        IReadOnlyList<int> reached = throttle.SuspendEncodes(registry, ["queue row 7"]);

        reached.Should().BeEquivalentTo([ProcessId, ProcessId + 1]);
        _suspender.Verify(s => s.Suspend(ProcessId), Times.Once);
        _suspender.Verify(s => s.Suspend(ProcessId + 1), Times.Once);
    }

    [Fact]
    public void PauseQueue_NoReachableProcess_LogsWarning()
    {
        RecordingLogger logger = new();
        ProcessThrottle throttle = BuildThrottle(logger);

        IReadOnlyList<int> reached = throttle.SuspendEncodes(
            new EncoderProcessRegistry(),
            ["queue row 7"]
        );

        reached.Should().BeEmpty();
        logger
            .Entries.Should()
            .ContainSingle(entry =>
                entry.Level == LogLevel.Warning && entry.Message.Contains("queue row 7")
            );
    }

    [Fact]
    public void PauseQueue_NothingRunning_LogsNoWarning()
    {
        RecordingLogger logger = new();
        ProcessThrottle throttle = BuildThrottle(logger);

        throttle.SuspendEncodes(new EncoderProcessRegistry(), []);

        logger.Entries.Should().NotContain(entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public void ResumeQueue_ResumesEveryRegisteredEncode()
    {
        ProcessThrottle throttle = BuildThrottle();
        EncoderProcessRegistry registry = new();
        registry.Register(JobId, ProcessId);
        throttle.SuspendEncodes(registry, ["queue row 7"]);

        throttle.ResumeEncodes(registry);

        throttle.IsSuspended(ProcessId).Should().BeFalse();
        _suspender.Verify(s => s.Resume(ProcessId), Times.Once);
    }

    private sealed class RecordingLogger : ILogger<ProcessThrottle>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
