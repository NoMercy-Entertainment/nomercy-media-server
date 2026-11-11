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

using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using NoMercy.Encoder.Commands;
using NoMercy.Encoder.Errors;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Encoder.Progress;
using NoMercy.NmSystem.Monitoring;

namespace NoMercy.Encoder.Execution;

public class FfmpegExecutor : IFfmpegExecutor
{
    private static readonly TimeSpan ProgressThrottleInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ExitGracePeriod = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PlaybackPollInterval = TimeSpan.FromSeconds(1);

    private readonly IProcessRunner _processRunner;
    private readonly ILogger<FfmpegExecutor> _logger;
    private readonly Func<bool>? _livePlaybackNeedsCapacity;
    private readonly MediaActivityMonitor? _activityMonitor;
    private readonly ProcessThrottle? _processThrottle;
    private readonly TimeSpan _playbackPollInterval = PlaybackPollInterval;

    public FfmpegExecutor(IProcessRunner processRunner, ILogger<FfmpegExecutor> logger)
    {
        _processRunner = processRunner;
        _logger = logger;
    }

    internal FfmpegExecutor(
        IProcessRunner processRunner,
        ILogger<FfmpegExecutor> logger,
        Func<bool> livePlaybackNeedsCapacity,
        MediaActivityMonitor activityMonitor,
        ProcessThrottle processThrottle,
        TimeSpan? playbackPollInterval = null
    )
        : this(processRunner, logger)
    {
        _livePlaybackNeedsCapacity = livePlaybackNeedsCapacity;
        _activityMonitor = activityMonitor;
        _processThrottle = processThrottle;
        _playbackPollInterval = playbackPollInterval ?? PlaybackPollInterval;
    }

    public async Task<ExecutionResult> ExecuteAsync(
        FfmpegCommand command,
        TimeSpan inputDuration,
        Action<EncodingProgress>? onProgress = null,
        string? correlationId = null,
        CancellationToken ct = default
    )
    {
        ProgressParser parser = new();
        Stopwatch stopwatch = Stopwatch.StartNew();
        DateTime lastProgressReport = DateTime.MinValue;

        // Metrics accumulators — sampled on every progress snapshot
        double speedSum = 0;
        double fpsSum = 0;
        double peakSpeed = 0;
        double peakFps = 0;
        long lastTotalSize = 0;
        int sampleCount = 0;

        // Kill signal: fires after a grace period once FFmpeg reports progress=end.
        // This prevents the process from hanging indefinitely after output is written.
        CancellationTokenSource killCts = new();
        bool hasProgressPipe = command.Arguments.Contains("pipe:1");
        int ffmpegPid = 0;
        using CancellationTokenSource playbackMonitorCts = new();
        Task? playbackMonitorTask = null;

        _logger.LogDebug(
            "[{CorrelationId}] Executing: {Executable} {Args}",
            correlationId,
            command.Executable,
            string.Join(" ", command.Arguments)
        );

        void OnStdOut(string line)
        {
            FfmpegProgressSnapshot? snapshot = parser.FeedLine(line);
            if (snapshot is null)
                return;

            // Collect metrics on every snapshot (not throttled)
            if (snapshot is { IsEnd: false, Speed: > 0 })
            {
                speedSum += snapshot.Speed;
                fpsSum += snapshot.Fps;
                peakSpeed = Math.Max(peakSpeed, snapshot.Speed);
                peakFps = Math.Max(peakFps, snapshot.Fps);
                lastTotalSize = snapshot.TotalSizeBytes;
                sampleCount++;
            }

            if (snapshot.IsEnd && hasProgressPipe)
            {
                lastTotalSize = snapshot.TotalSizeBytes;
                _logger.LogDebug(
                    "[{CorrelationId}] progress=end received, starting {Grace}s exit grace period",
                    correlationId,
                    ExitGracePeriod.TotalSeconds
                );
                killCts.CancelAfter(ExitGracePeriod);
            }

            if (onProgress is null)
                return;

            DateTime now = DateTime.UtcNow;
            bool throttled = now - lastProgressReport < ProgressThrottleInterval;

            if (!snapshot.IsEnd && throttled)
                return;

            lastProgressReport = now;

            double percent =
                inputDuration.TotalSeconds > 0
                    ? Math.Min(
                        100.0,
                        snapshot.OutTime.TotalSeconds / inputDuration.TotalSeconds * 100.0
                    )
                    : 0;

            TimeSpan? remaining =
                snapshot.Speed > 0 && inputDuration.TotalSeconds > 0
                    ? TimeSpan.FromSeconds(
                        (inputDuration.TotalSeconds - snapshot.OutTime.TotalSeconds)
                            / snapshot.Speed
                    )
                    : null;

            // Parse the raw bitrate string from FFmpeg (e.g. "1234.5kbits/s"). This
            // string rides EncodingProgress.Bitrate straight into the dashboard's
            // SignalR payload (EventBusProgressObserver), so it must stay
            // period-decimal regardless of the host's locale.
            string bitrateStr = snapshot.BitrateKbps.HasValue
                ? $"{snapshot.BitrateKbps.Value.ToString("F1", CultureInfo.InvariantCulture)}kbits/s"
                : "N/A";

            EncodingProgress progress = new(
                CorrelationId: correlationId ?? "",
                PercentComplete: percent,
                Elapsed: stopwatch.Elapsed,
                EstimatedRemaining: remaining,
                CurrentFps: snapshot.Fps,
                CurrentSpeed: snapshot.Speed,
                CurrentStage: "Execute",
                CurrentOperation: null,
                BitrateKbps: snapshot.BitrateKbps.HasValue ? (int)snapshot.BitrateKbps.Value : null,
                Bitrate: bitrateStr,
                ProcessId: ffmpegPid,
                CurrentTimeSeconds: snapshot.OutTime.TotalSeconds,
                DurationSeconds: inputDuration.TotalSeconds
            );

            onProgress(progress);
        }

        try
        {
            ProcessResult result = await _processRunner.RunAsync(
                command.Executable,
                command.Arguments,
                OnStdOut,
                null,
                command.WorkingDirectory,
                ct,
                killCts.Token,
                pid =>
                {
                    ffmpegPid = pid;
                    if (_livePlaybackNeedsCapacity is not null && _processThrottle is not null)
                        playbackMonitorTask = MonitorPlaybackAsync(pid, playbackMonitorCts.Token);
                }
            );

            stopwatch.Stop();

            if (result.IsSuccess)
            {
                ExecutionMetrics metrics = new(
                    AverageSpeed: sampleCount > 0 ? speedSum / sampleCount : 0,
                    AverageFps: sampleCount > 0 ? fpsSum / sampleCount : 0,
                    PeakSpeed: peakSpeed,
                    PeakFps: peakFps,
                    TotalSizeBytes: lastTotalSize
                );

                _logger.LogInformation(
                    "[{CorrelationId}] FFmpeg completed in {Duration} (avg {Speed:F2}x, {Fps:F1} fps)",
                    correlationId,
                    stopwatch.Elapsed,
                    metrics.AverageSpeed,
                    metrics.AverageFps
                );

                return new(
                    Success: true,
                    ExitCode: 0,
                    StdErr: result.StdErr,
                    Duration: stopwatch.Elapsed,
                    Error: null,
                    Metrics: metrics
                );
            }

            // ClassifyError still reads the whole stream; only the log is bounded.
            EncodingError error = ClassifyError(result.StdErr, result.ExitCode);
            _logger.LogError(
                "[{CorrelationId}] FFmpeg failed: exit={ExitCode} error={ErrorKind}\nstderr: {StdErr}",
                correlationId,
                result.ExitCode,
                error.Kind,
                FfmpegStderr.Tail(result.StdErr)
            );

            return new(
                Success: false,
                ExitCode: result.ExitCode,
                StdErr: result.StdErr,
                Duration: stopwatch.Elapsed,
                Error: error
            );
        }
        finally
        {
            await playbackMonitorCts.CancelAsync().ConfigureAwait(false);
            if (playbackMonitorTask is not null)
                await playbackMonitorTask.ConfigureAwait(false);
            killCts.Dispose();
        }
    }

    private async Task MonitorPlaybackAsync(int processId, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                bool needsCapacity = _livePlaybackNeedsCapacity!();
                if (needsCapacity)
                {
                    _activityMonitor?.Touch();
                    _processThrottle!.Suspend(processId);
                }
                else
                {
                    _processThrottle!.Resume(processId);
                }

                await Task.Delay(_playbackPollInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not yield background encode to live playback");
        }
        finally
        {
            try
            {
                _processThrottle!.Resume(processId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not resume background encode {ProcessId}", processId);
            }
        }
    }

    private static EncodingError ClassifyError(string stderr, int exitCode)
    {
        string lower = stderr.ToLowerInvariant();

        EncodingErrorKind kind = lower switch
        {
            _ when lower.Contains("no such file") => EncodingErrorKind.InputNotFound,
            _ when lower.Contains("invalid data found") => EncodingErrorKind.InputCorrupt,
            _ when lower.Contains("codec not currently supported") =>
                EncodingErrorKind.CodecUnavailable,
            _ when lower.Contains("encoder") && lower.Contains("not found") =>
                EncodingErrorKind.CodecUnavailable,
            _ when lower.Contains("device") && lower.Contains("cannot") =>
                EncodingErrorKind.HardwareFailure,
            _ when lower.Contains("no space left") => EncodingErrorKind.DiskFull,
            _ when lower.Contains("out of memory") => EncodingErrorKind.HardwareFailure,
            _ => EncodingErrorKind.ProcessCrashed,
        };

        return new(
            Kind: kind,
            Message: $"FFmpeg exited with code {exitCode}",
            FfmpegStderr: stderr.Length > 2000 ? stderr[^2000..] : stderr,
            StageName: "Execute",
            Recoverable: kind
                is EncodingErrorKind.HardwareFailure
                    or EncodingErrorKind.ProcessCrashed
        );
    }
}
