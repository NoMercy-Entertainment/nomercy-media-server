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

using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Moq;
using NoMercy.Api.Controllers.V1.Dashboard.Encoder;
using NoMercy.Authorization;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Distribution;
using Xunit;

namespace NoMercy.Tests.Api;

/// <summary>
/// Route-shape and functional tests for <see cref="CoordinatorDispatchController"/>.
///
/// Route tests verify the spec paths are declared. Functional tests exercise
/// controller logic against mocked dispatcher / progress-store — no HTTP
/// server is required.
/// </summary>
[Trait("Category", "CoordinatorDispatch")]
public class CoordinatorDispatchController_Tests
{
    // Shared owner GUID across all Tests.Api fixtures that seed
    // ClaimsPrincipalExtensions._users. Owner property returns the FIRST
    // u.Owner==true user, so all fixtures must agree on the same id —
    // otherwise test execution order can desync principal NameIdentifier
    // from the resolved Owner.Id and IsOwner() flips to false.
    private static readonly Guid OwnerUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // ── Owner seeding — reset+reseed unconditionally for order-independence ──

    private static void SeedOwnerInClaimsExtensions()
    {
        // Reset wipes whatever a prior test left in the process-wide static.
        // NoMercyApiFactory.InitializeAsync() replaces _users with DB users
        // whose Id differs from OwnerUserId; ClaimsPrincipalExtensionsTests
        // calls Reset() in Dispose(). Either leaves IsOwner() returning false.
        UserCache.Current.Reset();

        UserCache.Current.AddUser(
            new()
            {
                Id = OwnerUserId,
                Owner = true,
                Email = "dispatch-owner@local",
            }
        );
    }

    // ── Route shape ─────────────────────────────────────────────────────────

    private static IEnumerable<string> ControllerRoutes(Type controller) =>
        controller.GetCustomAttributes<RouteAttribute>().Select(a => a.Template);

    private static IEnumerable<string> ActionRoutes(Type controller, Type httpVerb) =>
        controller
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(m => m.GetCustomAttributes(httpVerb, inherit: false))
            .Cast<HttpMethodAttribute>()
            .Select(a => a.Template ?? string.Empty);

    [Fact]
    public void CoordinatorDispatch_Exposes_Distribution_Route()
    {
        IEnumerable<string> routes = ControllerRoutes(typeof(CoordinatorDispatchController));
        Assert.Contains(
            "api/v{version:apiVersion}/distribution/workers/dispatch",
            routes,
            StringComparer.OrdinalIgnoreCase
        );
    }

    [Fact]
    public void CoordinatorDispatch_Post_Action_Exists()
    {
        // The root POST has a null/empty template (no sub-path).
        IEnumerable<string> posts = ActionRoutes(
            typeof(CoordinatorDispatchController),
            typeof(HttpPostAttribute)
        );
        Assert.True(
            posts.Any(t => string.IsNullOrEmpty(t)),
            "POST / (root) action must exist on the dispatch controller"
        );
    }

    [Fact]
    public void CoordinatorDispatch_GetStatus_Action_Exists()
    {
        IEnumerable<string> gets = ActionRoutes(
            typeof(CoordinatorDispatchController),
            typeof(HttpGetAttribute)
        );
        Assert.Contains("{taskId}/status", gets, StringComparer.OrdinalIgnoreCase);
    }

    // ── Controller factory ──────────────────────────────────────────────────

    private static CoordinatorDispatchController BuildController(
        Mock<IWorkerDispatcher>? dispatcherMock = null,
        Mock<ITaskProgressStore>? storeMock = null,
        EncoderOptions? options = null
    )
    {
        SeedOwnerInClaimsExtensions();

        Mock<IWorkerDispatcher> d = dispatcherMock ?? new Mock<IWorkerDispatcher>();
        Mock<ITaskProgressStore> s = storeMock ?? new Mock<ITaskProgressStore>();

        if (dispatcherMock is null)
        {
            d.SetupGet(x => x.AvailableWorkerCount).Returns(1);
            d.Setup(x => x.DispatchAsync(It.IsAny<EncodeTask[]>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(
                    (EncodeTask[] tasks, CancellationToken _) =>
                        tasks
                            .Select(t => new DispatchResult(
                                TaskId: t.TaskId,
                                Success: true,
                                OutputPath: t.OutputPath,
                                Duration: TimeSpan.FromSeconds(1),
                                WorkerId: "mock-worker"
                            ))
                            .ToArray()
                );
        }

        if (storeMock is null)
            s.Setup(x => x.GetAll()).Returns([]);

        CoordinatorDispatchController controller = new(
            dispatcher: d.Object,
            progressStore: s.Object,
            encoderOptions: options ?? new EncoderOptions()
        );

        DefaultHttpContext ctx = new()
        {
            User = new(
                new ClaimsIdentity([new(ClaimTypes.NameIdentifier, OwnerUserId.ToString())], "test")
            ),
        };
        controller.ControllerContext = new() { HttpContext = ctx };
        return controller;
    }

    // ── Dispatch: validation ────────────────────────────────────────────────

    [Fact]
    public async Task Dispatch_EmptyTasks_ReturnsBadRequest()
    {
        CoordinatorDispatchController sut = BuildController();

        IActionResult result = await sut.Dispatch(new(Tasks: []), CancellationToken.None);

        ObjectResult obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, obj.StatusCode);
    }

    [Fact]
    public async Task Dispatch_NullTasks_ReturnsBadRequest()
    {
        CoordinatorDispatchController sut = BuildController();

        IActionResult result = await sut.Dispatch(new(Tasks: null), CancellationToken.None);

        ObjectResult obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, obj.StatusCode);
    }

    [Fact]
    public async Task Dispatch_ValidTasks_CallsDispatcher_ReturnsOk()
    {
        Mock<IWorkerDispatcher> mock = new();
        mock.SetupGet(d => d.AvailableWorkerCount).Returns(2);
        mock.Setup(d => d.DispatchAsync(It.IsAny<EncodeTask[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new(
                    TaskId: "t1",
                    Success: true,
                    OutputPath: "/out/t1",
                    Duration: TimeSpan.FromSeconds(2),
                    WorkerId: "worker-a"
                ),
            ]);

        CoordinatorDispatchController sut = BuildController(dispatcherMock: mock);

        IActionResult result = await sut.Dispatch(
            new(Tasks: [new(OutputPath: "/out/t1", TaskId: "t1")]),
            CancellationToken.None
        );

        Assert.IsType<OkObjectResult>(result);
        mock.Verify(
            d =>
                d.DispatchAsync(
                    It.Is<EncodeTask[]>(arr => arr.Length == 1),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task Dispatch_AutoAssignsTaskId_WhenNotProvided()
    {
        List<EncodeTask[]> captured = [];
        Mock<IWorkerDispatcher> mock = new();
        mock.SetupGet(d => d.AvailableWorkerCount).Returns(1);
        mock.Setup(d => d.DispatchAsync(It.IsAny<EncodeTask[]>(), It.IsAny<CancellationToken>()))
            .Callback<EncodeTask[], CancellationToken>((tasks, _) => captured.Add(tasks))
            .ReturnsAsync(
                (EncodeTask[] tasks, CancellationToken _) =>
                    tasks
                        .Select(t => new DispatchResult(
                            t.TaskId,
                            true,
                            t.OutputPath,
                            TimeSpan.Zero
                        ))
                        .ToArray()
            );

        CoordinatorDispatchController sut = BuildController(dispatcherMock: mock);

        await sut.Dispatch(
            new(Tasks: [new(OutputPath: "/out/auto", TaskId: null)]),
            CancellationToken.None
        );

        Assert.Single(captured);
        Assert.False(
            string.IsNullOrWhiteSpace(captured[0][0].TaskId),
            "A task ID must be auto-generated when the caller omits it"
        );
    }

    [Fact]
    public async Task Dispatch_MultipleTasks_AllPassedToDispatcher()
    {
        Mock<IWorkerDispatcher> mock = new();
        mock.SetupGet(d => d.AvailableWorkerCount).Returns(3);
        mock.Setup(d => d.DispatchAsync(It.IsAny<EncodeTask[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                (EncodeTask[] tasks, CancellationToken _) =>
                    tasks
                        .Select(t => new DispatchResult(
                            t.TaskId,
                            true,
                            t.OutputPath,
                            TimeSpan.Zero
                        ))
                        .ToArray()
            );

        CoordinatorDispatchController sut = BuildController(dispatcherMock: mock);

        await sut.Dispatch(
            new(
                Tasks:
                [
                    new(OutputPath: "/out/a", TaskId: "a"),
                    new(OutputPath: "/out/b", TaskId: "b"),
                    new(OutputPath: "/out/c", TaskId: "c"),
                ]
            ),
            CancellationToken.None
        );

        mock.Verify(
            d =>
                d.DispatchAsync(
                    It.Is<EncodeTask[]>(arr => arr.Length == 3),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task Dispatch_CarriesSourceDurationAndTimeRange_OntoTheTask()
    {
        List<EncodeTask[]> captured = [];
        Mock<IWorkerDispatcher> mock = new();
        mock.SetupGet(d => d.AvailableWorkerCount).Returns(1);
        mock.Setup(d => d.DispatchAsync(It.IsAny<EncodeTask[]>(), It.IsAny<CancellationToken>()))
            .Callback<EncodeTask[], CancellationToken>((tasks, _) => captured.Add(tasks))
            .ReturnsAsync(
                (EncodeTask[] tasks, CancellationToken _) =>
                    tasks
                        .Select(t => new DispatchResult(
                            t.TaskId,
                            true,
                            t.OutputPath,
                            TimeSpan.Zero
                        ))
                        .ToArray()
            );

        CoordinatorDispatchController sut = BuildController(dispatcherMock: mock);

        await sut.Dispatch(
            new(
                Tasks:
                [
                    new(
                        OutputPath: "/out/full",
                        TaskId: "full",
                        InputPath: "/media/source.mkv",
                        SourceDurationSeconds: 600
                    ),
                    new(
                        OutputPath: "/out/slice",
                        TaskId: "slice",
                        TaskType: EncodeTaskType.TimeChunk,
                        TimeRangeStartSeconds: 120,
                        TimeRangeDurationSeconds: 60
                    ),
                ]
            ),
            CancellationToken.None
        );

        EncodeTask full = Assert.Single(captured)[0];
        Assert.Equal("/media/source.mkv", full.InputPath);
        Assert.Equal(TimeSpan.FromMinutes(10), full.SourceDuration);
        Assert.Null(full.TimeRangeDuration);

        EncodeTask slice = captured[0][1];
        Assert.Equal(TimeSpan.FromSeconds(120), slice.TimeRangeStart);
        Assert.Equal(TimeSpan.FromSeconds(60), slice.TimeRangeDuration);
        Assert.Null(slice.SourceDuration);
    }

    // ── GetTaskStatus ─────────────────────────────────────────────────────────

    [Fact]
    public void GetTaskStatus_UnknownTask_ReturnsNotFound()
    {
        Mock<ITaskProgressStore> store = new();
        store.Setup(s => s.GetAll()).Returns([]);

        CoordinatorDispatchController sut = BuildController(storeMock: store);

        IActionResult result = sut.GetTaskStatus("does-not-exist");

        ObjectResult obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, obj.StatusCode);
    }

    [Fact]
    public void GetTaskStatus_KnownTask_ReturnsOk()
    {
        TaskProgressSnapshot snapshot = new(
            TaskId: "t-known",
            WorkerId: "w1",
            PercentComplete: 55.0,
            CurrentFps: 30,
            CurrentSpeed: 1.2,
            CurrentStage: "encoding",
            ElapsedSeconds: 60,
            EstimatedRemainingSeconds: 45,
            CurrentTimeSeconds: 120,
            DurationSeconds: 300,
            ReceivedAtUtc: DateTime.UtcNow
        );

        Mock<ITaskProgressStore> store = new();
        store.Setup(s => s.GetAll()).Returns([snapshot]);

        CoordinatorDispatchController sut = BuildController(storeMock: store);

        IActionResult result = sut.GetTaskStatus("t-known");

        Assert.IsType<OkObjectResult>(result);
    }
}
