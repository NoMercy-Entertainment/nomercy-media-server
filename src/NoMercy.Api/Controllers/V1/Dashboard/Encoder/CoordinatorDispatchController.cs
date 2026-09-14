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

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Distribution;

namespace NoMercy.Api.Controllers.V1.Dashboard.Encoder;

/// <summary>
/// Coordinator-side dispatch controller. Accepts an encode job from the
/// dashboard or queue, fans tasks out to registered remote workers via
/// <see cref="IWorkerDispatcher"/>, and returns the initial assignment
/// summary. In standalone / worker mode the dispatcher transparently
/// falls back to local execution — this endpoint behaves identically.
///
/// Status polling is available via GET dispatch/{taskId}/status which
/// proxies the <see cref="ITaskProgressStore"/>.
/// </summary>
[ApiController]
[Tags("Distribution Dispatch")]
[ApiVersion(1.0)]
[Authorize(Policy = "Owner")]
[Route("api/v{version:apiVersion}/distribution/workers/dispatch")]
public class CoordinatorDispatchController(
    IWorkerDispatcher dispatcher,
    ITaskProgressStore progressStore,
    EncoderOptions encoderOptions
) : BaseController
{
    /// <summary>
    /// Dispatch an encode job. The dispatcher fans tasks across registered
    /// workers (or runs locally when no remote workers are available).
    /// Returns the list of dispatched task IDs and the worker assignment
    /// summary so the caller knows which worker is handling which task.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Dispatch(
        [FromBody] DispatchEncodeJobRequest request,
        CancellationToken ct
    )
    {
        if (request.Tasks is not { Count: > 0 })
            return BadRequestResponse("tasks must be a non-empty array");

        EncodeTask[] tasks =
        [
            .. request.Tasks.Select(t => new EncodeTask(
                TaskId: t.TaskId ?? Guid.NewGuid().ToString("N"),
                Command: new(
                    Executable: "ffmpeg",
                    Arguments: t.Arguments?.ToArray() ?? [],
                    WorkingDirectory: null
                ),
                OutputPath: t.OutputPath,
                Type: t.TaskType
            )),
        ];

        DispatchResult[] results = await dispatcher.DispatchAsync(tasks, ct).ConfigureAwait(false);

        return Ok(
            new
            {
                available_workers = dispatcher.AvailableWorkerCount,
                distribution_enabled = encoderOptions.IsDistributedEncodingEnabled,
                dispatched_count = results.Length,
                tasks = results.Select(r => new
                {
                    task_id = r.TaskId,
                    worker_id = r.WorkerId,
                    success = r.Success,
                    output_path = r.OutputPath,
                    duration_seconds = r.Duration.TotalSeconds,
                    error = r.Error,
                }),
            }
        );
    }

    /// <summary>
    /// Poll the latest progress snapshot for a dispatched task.
    /// Returns 404 when no progress has been received yet (the task
    /// may still be queued or the worker hasn't pushed its first tick).
    /// </summary>
    [HttpGet("{taskId}/status")]
    public IActionResult GetTaskStatus(string taskId)
    {
        TaskProgressSnapshot? snapshot = progressStore
            .GetAll()
            .FirstOrDefault(s => s.TaskId == taskId);

        if (snapshot is null)
            return NotFoundResponse($"No progress recorded for task '{taskId}'");

        return Ok(
            new
            {
                task_id = snapshot.TaskId,
                worker_id = snapshot.WorkerId,
                percent_complete = snapshot.PercentComplete,
                current_fps = snapshot.CurrentFps,
                current_speed = snapshot.CurrentSpeed,
                current_stage = snapshot.CurrentStage,
                elapsed_seconds = snapshot.ElapsedSeconds,
                estimated_remaining_seconds = snapshot.EstimatedRemainingSeconds,
                current_time_seconds = snapshot.CurrentTimeSeconds,
                duration_seconds = snapshot.DurationSeconds,
                received_at_utc = snapshot.ReceivedAtUtc,
            }
        );
    }
}
