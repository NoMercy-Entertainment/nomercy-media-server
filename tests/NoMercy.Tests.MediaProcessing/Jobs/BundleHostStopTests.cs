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
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Database;
using NoMercy.Encoder.Decomposition;
using NoMercy.Encoder.Orchestration;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Profiles;
using NoMercy.Encoder.Progress;
using NoMercy.Events;
using NoMercy.Events.Encoding;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.Tests.Common;

namespace NoMercy.Tests.MediaProcessing.Jobs;

/// <summary>
/// #644: a host stop that cancels a bundle encode is not a failed bundle. The
/// cancel has to leave the job, so the queue worker releases the reservation
/// (QueueWorkerShutdownRecoveryTests pins that side) and refunds the attempt.
/// </summary>
[Collection("EventBusProvider")]
[Trait("Category", "Unit")]
public class BundleHostStopTests
{
    private static readonly DecomposedTask Task1 = new(
        TaskId: "task-1",
        ParentJobId: 1,
        GroupTag: "group-1",
        Kind: EncodeTaskKind.Video,
        OutputIndex: 0,
        Resources: null
    );

    private static VideoEncodeJob BuildJob() =>
        new()
        {
            Id = "42",
            FolderId = Ulid.NewUlid(),
            LibraryId = Ulid.NewUlid(),
            InputFile = "/media/source/movie.mkv",
            LoggerFactory = NullLoggerFactory.Instance,
        };

    private static EncodingRequest BuildRequest() =>
        new(
            InputPath: "/media/source/movie.mkv",
            OutputDirectory: "/media/output",
            Profile: new(
                Id: Ulid.NewUlid(),
                Name: "Test HLS",
                Container: Container.HlsTs,
                Video: null,
                Audio: [],
                Subtitles: []
            )
        );

    private static Mock<IEncodingOrchestrator> OrchestratorThrowing(
        Exception exception,
        List<EncodingRequest> seen
    )
    {
        Mock<IEncodingOrchestrator> orchestrator = new();
        orchestrator
            .Setup(o =>
                o.EncodeAsync(
                    It.IsAny<EncodingRequest>(),
                    It.IsAny<DecomposedTask>(),
                    It.IsAny<IProgressObserver?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<EncodingRequest, DecomposedTask, IProgressObserver?, CancellationToken>(
                (request, _, _, _) => seen.Add(request)
            )
            .ThrowsAsync(exception);
        return orchestrator;
    }

    private static async Task<(
        List<EncodeTaskCompletedEvent> Outcomes,
        List<EncodingFailedEvent> FailedCards,
        Exception? Thrown,
        (bool Success, string? Error)? Returned
    )> RunAsync(Exception exception, CancellationToken hostStopping, List<EncodingRequest> seen)
    {
        FieldInfo instance = typeof(EventBusProvider).GetField(
            "_instance",
            BindingFlags.NonPublic | BindingFlags.Static
        )!;
        IEventBus? previous = (IEventBus?)instance.GetValue(null);

        List<EncodeTaskCompletedEvent> outcomes = [];
        List<EncodingFailedEvent> failedCards = [];
        Mock<IEventBus> bus = new();
        bus.Setup(b =>
                b.PublishAsync(It.IsAny<EncodeTaskCompletedEvent>(), It.IsAny<CancellationToken>())
            )
            .Callback<EncodeTaskCompletedEvent, CancellationToken>((e, _) => outcomes.Add(e))
            .Returns(Task.CompletedTask);
        bus.Setup(b =>
                b.PublishAsync(It.IsAny<EncodingFailedEvent>(), It.IsAny<CancellationToken>())
            )
            .Callback<EncodingFailedEvent, CancellationToken>((e, _) => failedCards.Add(e))
            .Returns(Task.CompletedTask);
        EventBusProvider.Configure(bus.Object);

        try
        {
            await using MediaContext context = new();
            Mock<IProgressObserver> observer = new();
            VideoEncodeJob job = BuildJob();
            Mock<IEncodingOrchestrator> orchestrator = OrchestratorThrowing(exception, seen);
            try
            {
                (bool, string?) returned = await job.EncodeBundleAsync(
                    orchestrator.Object,
                    BuildRequest(),
                    Task1,
                    observer.Object,
                    context,
                    BuildRequest().Profile,
                    hostStopping
                );
                return (outcomes, failedCards, null, returned);
            }
            catch (Exception ex)
            {
                return (outcomes, failedCards, ex, null);
            }
        }
        finally
        {
            instance.SetValue(null, previous);
        }
    }

    [Fact]
    public async Task HostStopCancel_LeavesTheJob_WithNoFailedOutcomeAndNoFailedCard()
    {
        using CancellationTokenSource hostStop = new();
        await hostStop.CancelAsync();
        List<EncodingRequest> seen = [];

        (
            List<EncodeTaskCompletedEvent> outcomes,
            List<EncodingFailedEvent> failedCards,
            Exception? thrown,
            (bool, string?)? returned
        ) = await RunAsync(new OperationCanceledException(hostStop.Token), hostStop.Token, seen);

        thrown.Should().BeAssignableTo<OperationCanceledException>("the queue worker releases it");
        returned.Should().BeNull("a host stop is not a failed bundle");
        outcomes.Should().BeEmpty("no failed outcome row may be written for a host stop");
        failedCards.Should().BeEmpty("no failed card may be published for a host stop");
        seen.Should().ContainSingle().Which.HostStopping.Should().Be(hostStop.Token);
    }

    [Fact]
    public async Task CancelWithoutHostStop_StaysAFailedBundle()
    {
        using CancellationTokenSource userCancel = new();
        await userCancel.CancelAsync();

        (
            List<EncodeTaskCompletedEvent> outcomes,
            _,
            Exception? thrown,
            (bool Success, string? Error)? returned
        ) = await RunAsync(
            new OperationCanceledException(userCancel.Token),
            CancellationToken.None,
            []
        );

        thrown.Should().BeNull();
        returned!.Value.Success.Should().BeFalse();
        outcomes.Should().ContainSingle().Which.Success.Should().BeFalse();
    }

    [Fact]
    public async Task RealFailureWhileHostStops_StaysAFailedBundle()
    {
        using CancellationTokenSource hostStop = new();
        await hostStop.CancelAsync();

        (
            List<EncodeTaskCompletedEvent> outcomes,
            _,
            Exception? thrown,
            (bool Success, string? Error)? returned
        ) = await RunAsync(new InvalidOperationException("ffmpeg exited 1"), hostStop.Token, []);

        thrown.Should().BeNull();
        returned!.Value.Error.Should().Be("ffmpeg exited 1");
        outcomes.Should().ContainSingle().Which.Error.Should().Be("ffmpeg exited 1");
    }

    // Source text: the coordinator and initial-run catches need a live MediaContext
    // and queue to drive, so the filter that keeps the failed card off is pinned here.
    [Theory]
    [InlineData("private async Task HandleCoordinatorWakeUpAsync")]
    [InlineData("private async Task HandleInitialRunAsync")]
    public void FailedCardCatch_SkipsAHostStop(string method)
    {
        string source = File.ReadAllText(RepoPaths.SourceFile("VideoEncodeJob.cs"));
        int start = source.IndexOf(method, StringComparison.Ordinal);
        start.Should().BeGreaterThan(0);
        string body = source[start..];
        int card = body.IndexOf(
            "EncoderCardTerminator.PublishFailedAsync",
            StringComparison.Ordinal
        );
        card.Should().BeGreaterThan(0);
        body[..card].Should().Contain("when (!IsHostStop(ex, _shutdownToken))");
    }

    [Fact]
    public async Task HostStop_IsACancelWhileTheHostTokenIsCancelled()
    {
        using CancellationTokenSource hostStop = new();
        OperationCanceledException cancel = new();

        VideoEncodeJob.IsHostStop(cancel, hostStop.Token).Should().BeFalse();
        await hostStop.CancelAsync();
        VideoEncodeJob.IsHostStop(cancel, hostStop.Token).Should().BeTrue();
        VideoEncodeJob
            .IsHostStop(new InvalidOperationException(), hostStop.Token)
            .Should()
            .BeFalse();
    }
}
