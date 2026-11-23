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

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Api.Controllers.V1.Dashboard.Admin;
using NoMercy.Api.DTOs.Common;
using NoMercy.Encoder.Execution;
using NoMercy.Encoder.Progress;
using NoMercy.MediaProcessing.Jobs.MediaJobs.Support;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

/// <summary>
/// The encoder card pauses one encode with <c>pause/{payload_id}</c>. For a video
/// encode the payload id is the movie or episode id, and that is the key the
/// encode's progress observer registers its ffmpeg pid under.
/// </summary>
[Trait("Category", "Tasks")]
public class TasksControllerPauseTaskTests
{
    private const int PayloadId = 1001;
    private const int ProcessId = 4242;

    private readonly Mock<IProcessSuspender> _suspender = new();
    private readonly EncoderProcessRegistry _registry = new();
    private readonly ProcessThrottle _throttle;
    private readonly TasksController _controller;

    public TasksControllerPauseTaskTests()
    {
        _throttle = new(NullLogger<ProcessThrottle>.Instance, _suspender.Object);
        _controller = new(
            null!,
            null!,
            null!,
            null!,
            _registry,
            _throttle,
            null!,
            null!,
            null!,
            null!,
            null!
        );
    }

    private void RunEncodeObservedAs(int jobId)
    {
        EventBusProgressObserver observer = new(jobId: jobId, title: "Test", registry: _registry);
        observer.OnProgress(
            new EncodingProgress(
                CorrelationId: "c",
                PercentComplete: 10.0,
                Elapsed: TimeSpan.FromSeconds(1),
                EstimatedRemaining: null,
                CurrentFps: 24.0,
                CurrentSpeed: 1.0,
                CurrentStage: "Execute",
                CurrentOperation: null,
                ProcessId: ProcessId
            )
        );
    }

    private static string StatusOf(IActionResult result) =>
        ((StatusResponseDto<string>)((OkObjectResult)result).Value!).Status;

    [Fact]
    public void PauseTask_FindsProcessByDashboardId()
    {
        RunEncodeObservedAs(PayloadId);

        IActionResult result = _controller.PauseTask(PayloadId);

        StatusOf(result).Should().Be("success");
        _throttle.IsSuspended(ProcessId).Should().BeTrue();
        _suspender.Verify(s => s.Suspend(ProcessId), Times.Once);
    }

    [Fact]
    public void ResumeTask_WakesTheProcessPausedByDashboardId()
    {
        RunEncodeObservedAs(PayloadId);
        _controller.PauseTask(PayloadId);

        IActionResult result = _controller.ResumeTask(PayloadId);

        StatusOf(result).Should().Be("success");
        _throttle.IsSuspended(ProcessId).Should().BeFalse();
        _suspender.Verify(s => s.Resume(ProcessId), Times.Once);
    }
}
