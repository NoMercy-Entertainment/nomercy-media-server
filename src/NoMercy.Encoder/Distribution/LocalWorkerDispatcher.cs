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
using NoMercy.Encoder.Errors;
using NoMercy.Encoder.Execution;
using NoMercy.Encoder.Progress;

namespace NoMercy.Encoder.Distribution;

/// <summary>
/// Runs every task locally on the same machine via <see cref="IFfmpegExecutor"/>.
/// Default when no remote workers are configured — matches the single-machine
/// behavior today. Tasks run sequentially so GPU / CPU contention stays bounded;
/// the outer job queue already serializes encodes and we don't want to punch
/// through that by running multiple ffmpeg processes in parallel here.
///
/// Progress events from the executor get forwarded to the injected
/// <see cref="ITaskProgressSink"/> so remote workers can push updates to
/// the coordinator. On standalone installs the sink is a no-op.
/// </summary>
public class LocalWorkerDispatcher(
    IFfmpegExecutor executor,
    ITaskProgressSink progressSink,
    ISourceDurationProbe sourceDurationProbe,
    ILogger<LocalWorkerDispatcher> logger
) : IWorkerDispatcher
{
    // Overloads for callers that don't want to plumb a progress sink or a
    // duration probe — keep the test-bootstrap shapes unchanged.
    public LocalWorkerDispatcher(IFfmpegExecutor executor, ILogger<LocalWorkerDispatcher> logger)
        : this(executor, new NullTaskProgressSink(), new NullSourceDurationProbe(), logger) { }

    public LocalWorkerDispatcher(
        IFfmpegExecutor executor,
        ITaskProgressSink progressSink,
        ILogger<LocalWorkerDispatcher> logger
    )
        : this(executor, progressSink, new NullSourceDurationProbe(), logger) { }

    public int AvailableWorkerCount => 1;

    public async Task<DispatchResult[]> DispatchAsync(EncodeTask[] tasks, CancellationToken ct)
    {
        DispatchResult[] results = new DispatchResult[tasks.Length];

        for (int i = 0; i < tasks.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            EncodeTask task = tasks[i];

            logger.LogInformation(
                "Dispatching task {TaskId} ({Type}) locally → {Output}",
                task.TaskId,
                task.Type,
                task.OutputPath
            );

            try
            {
                // Forward every progress tick to the configured sink. Use a
                // local to capture task.TaskId so the Action doesn't close
                // over the loop variable.
                string currentTaskId = task.TaskId;
                Action<EncodingProgress> onProgress = progress =>
                {
                    try
                    {
                        progressSink.Report(currentTaskId, progress);
                    }
                    catch (Exception ex)
                    {
                        // Progress reporting must NEVER fail the encode.
                        logger.LogDebug(
                            ex,
                            "Progress sink failed for task {TaskId}",
                            currentTaskId
                        );
                    }
                };

                TimeSpan inputDuration = await ResolveInputDurationAsync(task, ct);
                ExecutionResult exec = await executor.ExecuteAsync(
                    task.Command,
                    inputDuration,
                    onProgress,
                    task.TaskId,
                    ct
                );

                results[i] = new(
                    TaskId: task.TaskId,
                    Success: exec.Success,
                    OutputPath: task.OutputPath,
                    Duration: exec.Duration,
                    Error: exec.Success ? null : ErrorMessage(exec.Error)
                );

                if (!exec.Success)
                {
                    logger.LogWarning(
                        "Local task {TaskId} failed: {Error}",
                        task.TaskId,
                        results[i].Error
                    );
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Local task {TaskId} threw", task.TaskId);
                results[i] = new(
                    TaskId: task.TaskId,
                    Success: false,
                    OutputPath: task.OutputPath,
                    Duration: TimeSpan.Zero,
                    Error: ex.Message
                );
            }
        }

        return results;
    }

    /// <summary>
    /// The denominator ffmpeg progress is measured against. A time-chunked
    /// task encodes only its slice; a full-source task runs against the
    /// duration the coordinator declared, or the probed input when it
    /// declared none. Zero means "no percentage" — never fail the encode
    /// over a source we could not measure.
    /// </summary>
    private async Task<TimeSpan> ResolveInputDurationAsync(EncodeTask task, CancellationToken ct)
    {
        if (task.TimeRangeDuration is { } range)
            return range;

        if (task.SourceDuration is { } declared)
            return declared;

        if (string.IsNullOrWhiteSpace(task.InputPath))
            return TimeSpan.Zero;

        TimeSpan? probed = await sourceDurationProbe.ProbeAsync(task.InputPath, ct);
        if (probed is null)
            logger.LogDebug(
                "Task {TaskId} has no measurable source duration; progress will stay at 0 %",
                task.TaskId
            );

        return probed ?? TimeSpan.Zero;
    }

    private static string? ErrorMessage(EncodingError? error) =>
        error is null ? null : $"{error.Kind}: {error.Message}";
}
