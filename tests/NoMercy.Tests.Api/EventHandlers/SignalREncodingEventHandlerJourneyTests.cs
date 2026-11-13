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
using NoMercy.Api.DTOs.Encoding;
using NoMercy.Api.EventHandlers;
using NoMercy.Authorization;
using NoMercy.Database.Models.Users;
using NoMercy.Events;
using NoMercy.Events.Encoding;
using NoMercy.Events.Media;
using NoMercy.Networking.Messaging;
using Xunit;

namespace NoMercy.Tests.Api.EventHandlers;

public class SignalREncodingEventHandlerJourneyTests
{
    private static (
        InMemoryEventBus bus,
        Mock<IClientMessenger> messengerMock,
        SignalREncodingEventHandler handler
    ) BuildChain()
    {
        InMemoryEventBus bus = new();
        Mock<IClientMessenger> messengerMock = new(MockBehavior.Strict);
        messengerMock
            .Setup(m =>
                m.SendToModerators(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>())
            )
            .Returns(Task.CompletedTask);
        SignalREncodingEventHandler handler = new(
            NullLogger<SignalREncodingEventHandler>.Instance,
            bus,
            messengerMock.Object
        );
        return (bus, messengerMock, handler);
    }

    [Fact]
    public async Task EncodingEvents_AreNotSentToEveryDashboardConnection()
    {
        (
            InMemoryEventBus bus,
            Mock<IClientMessenger> messengerMock,
            SignalREncodingEventHandler handler
        ) = BuildChain();
        using SignalREncodingEventHandler _ = handler;

        await bus.PublishAsync(
            new EncodingStartedEvent
            {
                JobId = 7,
                InputPath = "/input/movie.mkv",
                OutputPath = "/output/movie/",
                ProfileName = "HLS-1080p",
            }
        );

        messengerMock.Verify(
            m => m.SendToAll(It.IsAny<string>(), "dashboardHub", It.IsAny<object>()),
            Times.Never
        );
    }

    [Fact]
    public async Task EncodingStarted_PublishViaRealBus_CallsSendToAll_WithDashboardHub_AndCorrectMethodAndPayload()
    {
        (
            InMemoryEventBus bus,
            Mock<IClientMessenger> messengerMock,
            SignalREncodingEventHandler handler
        ) = BuildChain();
        using SignalREncodingEventHandler _ = handler;

        EncodingStartedEvent publishedEvent = new()
        {
            JobId = 7,
            InputPath = "/input/movie.mkv",
            OutputPath = "/output/movie/",
            ProfileName = "HLS-1080p",
        };

        await bus.PublishAsync(publishedEvent);

        messengerMock.Verify(
            m =>
                m.SendToModerators(
                    "EncodingStarted",
                    "dashboardHub",
                    It.Is<EncodingStartedDto>(dto =>
                        dto.Id == 7
                        && dto.InputPath == "/input/movie.mkv"
                        && dto.OutputPath == "/output/movie/"
                        && dto.ProfileName == "HLS-1080p"
                    )
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task EncodingProgressUpdated_PublishViaRealBus_CallsSendToAll_WithDashboardHub_AndCorrectMethodAndPayload()
    {
        (
            InMemoryEventBus bus,
            Mock<IClientMessenger> messengerMock,
            SignalREncodingEventHandler handler
        ) = BuildChain();
        using SignalREncodingEventHandler _ = handler;

        EncodingProgressUpdatedEvent publishedEvent = new()
        {
            JobId = 12,
            Percentage = 42.5,
            Elapsed = TimeSpan.FromMinutes(4),
            Estimated = TimeSpan.FromMinutes(6),
            Fps = 29.97,
            Speed = 1.5,
            BitrateKbps = 4500,
        };

        await bus.PublishAsync(publishedEvent);

        messengerMock.Verify(
            m =>
                m.SendToModerators(
                    "EncodingProgress",
                    "dashboardHub",
                    It.Is<EncodingProgressDto>(dto =>
                        dto.Id == 12
                        && dto.Percentage == 42.5
                        && dto.Elapsed == TimeSpan.FromMinutes(4).TotalSeconds
                        && dto.Estimated == TimeSpan.FromMinutes(6).TotalSeconds
                        && dto.Fps == 29.97
                        && dto.Speed == 1.5
                        && dto.BitrateKbps == 4500
                    )
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task EncodingCompleted_PublishViaRealBus_CallsSendToAll_WithDashboardHub_AndCorrectMethodAndPayload()
    {
        (
            InMemoryEventBus bus,
            Mock<IClientMessenger> messengerMock,
            SignalREncodingEventHandler handler
        ) = BuildChain();
        using SignalREncodingEventHandler _ = handler;

        EncodingCompletedEvent publishedEvent = new()
        {
            JobId = 33,
            OutputPath = "/output/movie/playlist.m3u8",
            Duration = TimeSpan.FromMinutes(118),
        };

        await bus.PublishAsync(publishedEvent);

        messengerMock.Verify(
            m =>
                m.SendToModerators(
                    "EncodingCompleted",
                    "dashboardHub",
                    It.Is<EncodingCompletedDto>(dto =>
                        dto.Id == 33
                        && dto.OutputPath == "/output/movie/playlist.m3u8"
                        && dto.Duration == TimeSpan.FromMinutes(118).TotalSeconds
                    )
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task A_failed_encode_reaches_the_owners_bell_with_the_incomplete_encodes_page()
    {
        InMemoryEventBus bus = new();
        Mock<IClientMessenger> messenger = new();
        Guid ownerId = Guid.NewGuid();
        Mock<IUserCache> users = new();
        users
            .SetupGet(cache => cache.Users)
            .Returns([new User { Id = Guid.NewGuid() }, new User { Id = ownerId, Owner = true }]);

        List<UserNotifiedEvent> notices = [];
        using IDisposable listening = bus.Subscribe<UserNotifiedEvent>(
            (notice, _) =>
            {
                notices.Add(notice);
                return Task.CompletedTask;
            }
        );
        using SignalREncodingEventHandler handler = new(
            NullLogger<SignalREncodingEventHandler>.Instance,
            bus,
            messenger.Object,
            users.Object
        );

        await bus.PublishAsync(
            new EncodingFailedEvent
            {
                JobId = 7,
                InputPath = "/input/example.mkv",
                ErrorMessage = "invalid codec",
                ExceptionType = "InvalidOperationException",
            }
        );

        UserNotifiedEvent sent = Assert.Single(notices);
        Assert.Equal(ownerId, sent.UserId);
        Assert.Equal("error", sent.Type);
        Assert.Equal("/dashboard/incomplete-encodes", sent.Route);
        Assert.Contains("example.mkv", sent.Message);
    }

    [Fact]
    public async Task EncodingFailed_PublishViaRealBus_CallsSendToAll_WithDashboardHub_AndCorrectMethodAndPayload()
    {
        (
            InMemoryEventBus bus,
            Mock<IClientMessenger> messengerMock,
            SignalREncodingEventHandler handler
        ) = BuildChain();
        using SignalREncodingEventHandler _ = handler;

        EncodingFailedEvent publishedEvent = new()
        {
            JobId = 99,
            InputPath = "/input/corrupt.mkv",
            ErrorMessage = "FFmpeg exited with code 1: invalid codec",
            ExceptionType = "InvalidOperationException",
        };

        await bus.PublishAsync(publishedEvent);

        messengerMock.Verify(
            m =>
                m.SendToModerators(
                    "EncodingFailed",
                    "dashboardHub",
                    It.Is<EncodingFailedDto>(dto =>
                        dto.Id == 99
                        && dto.InputPath == "/input/corrupt.mkv"
                        && dto.ErrorMessage == "FFmpeg exited with code 1: invalid codec"
                        && dto.ExceptionType == "InvalidOperationException"
                    )
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task EncodingStageChanged_PublishViaRealBus_CallsSendToAll_WithDashboardHub_AndEncoderProgressMethod()
    {
        (
            InMemoryEventBus bus,
            Mock<IClientMessenger> messengerMock,
            SignalREncodingEventHandler handler
        ) = BuildChain();
        using SignalREncodingEventHandler _ = handler;

        List<string> videoStreams = ["h264", "hevc"];
        List<string> audioStreams = ["aac", "ac3"];
        List<string> subtitleStreams = ["srt"];

        EncodingStageChangedEvent publishedEvent = new()
        {
            JobId = 55,
            Status = "encoding",
            Title = "Building Video",
            Message = "Processing 1080p stream",
            BaseFolder = "/media/movies",
            ShareBasePath = "/share/movies",
            VideoStreams = videoStreams,
            AudioStreams = audioStreams,
            SubtitleStreams = subtitleStreams,
            HasGpu = true,
            IsHdr = false,
        };

        await bus.PublishAsync(publishedEvent);

        messengerMock.Verify(
            m =>
                m.SendToModerators(
                    "encoder-progress",
                    "dashboardHub",
                    It.Is<EncodingStageChangedDto>(dto =>
                        dto.Id == 55
                        && dto.Status == "encoding"
                        && dto.Title == "Building Video"
                        && dto.Message == "Processing 1080p stream"
                        && dto.BaseFolder == "/media/movies"
                        && dto.SharePath == "/share/movies"
                        && dto.HasGpu == true
                        && dto.IsHdr == false
                        && dto.VideoStreams.SequenceEqual(videoStreams)
                        && dto.AudioStreams.SequenceEqual(audioStreams)
                        && dto.SubtitleStreams.SequenceEqual(subtitleStreams)
                    )
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task EncodingProgressBroadcasted_PublishViaRealBus_CallsSendToAll_WithDashboardHub_AndEncoderProgressMethod_WithRawProgressData()
    {
        (
            InMemoryEventBus bus,
            Mock<IClientMessenger> messengerMock,
            SignalREncodingEventHandler handler
        ) = BuildChain();
        using SignalREncodingEventHandler _ = handler;

        object progressData = new { percentage = 75.0, fps = 24.0 };

        EncodingProgressBroadcastedEvent publishedEvent = new() { ProgressData = progressData };

        await bus.PublishAsync(publishedEvent);

        messengerMock.Verify(
            m => m.SendToModerators("encoder-progress", "dashboardHub", progressData),
            Times.Once
        );
    }

    [Fact]
    public async Task AllSixEvents_EachCallSendToAll_ExactlyOnce_AndOnlyForTheirOwnEvent()
    {
        (
            InMemoryEventBus bus,
            Mock<IClientMessenger> messengerMock,
            SignalREncodingEventHandler handler
        ) = BuildChain();
        using SignalREncodingEventHandler _ = handler;

        await bus.PublishAsync(
            new EncodingStartedEvent
            {
                JobId = 1,
                InputPath = "/a.mkv",
                OutputPath = "/out/",
                ProfileName = "x264",
            }
        );

        await bus.PublishAsync(
            new EncodingProgressUpdatedEvent
            {
                JobId = 1,
                Percentage = 25.0,
                Elapsed = TimeSpan.FromSeconds(30),
            }
        );

        await bus.PublishAsync(
            new EncodingCompletedEvent
            {
                JobId = 1,
                OutputPath = "/out/playlist.m3u8",
                Duration = TimeSpan.FromMinutes(90),
            }
        );

        await bus.PublishAsync(
            new EncodingFailedEvent
            {
                JobId = 2,
                InputPath = "/b.mkv",
                ErrorMessage = "codec not found",
            }
        );

        await bus.PublishAsync(
            new EncodingStageChangedEvent
            {
                JobId = 3,
                Status = "analyzing",
                Title = "Analysis",
                Message = "Probing streams",
            }
        );

        await bus.PublishAsync(
            new EncodingProgressBroadcastedEvent { ProgressData = new { stage = "broadcast" } }
        );

        messengerMock.Verify(
            m => m.SendToModerators("EncodingStarted", "dashboardHub", It.IsAny<object>()),
            Times.Once
        );
        messengerMock.Verify(
            m => m.SendToModerators("EncodingProgress", "dashboardHub", It.IsAny<object>()),
            Times.Once
        );
        messengerMock.Verify(
            m => m.SendToModerators("EncodingCompleted", "dashboardHub", It.IsAny<object>()),
            Times.Once
        );
        messengerMock.Verify(
            m => m.SendToModerators("EncodingFailed", "dashboardHub", It.IsAny<object>()),
            Times.Once
        );
        messengerMock.Verify(
            m => m.SendToModerators("encoder-progress", "dashboardHub", It.IsAny<object>()),
            Times.Exactly(2)
        );
    }

    [Fact]
    public async Task Dispose_AfterSubscription_StopsAllSendToAllCalls()
    {
        InMemoryEventBus bus = new();
        Mock<IClientMessenger> messengerMock = new(MockBehavior.Strict);
        messengerMock
            .Setup(m =>
                m.SendToModerators(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>())
            )
            .Returns(Task.CompletedTask);

        SignalREncodingEventHandler handler = new(
            NullLogger<SignalREncodingEventHandler>.Instance,
            bus,
            messengerMock.Object
        );

        await bus.PublishAsync(
            new EncodingStartedEvent
            {
                JobId = 1,
                InputPath = "/a.mkv",
                OutputPath = "/out/",
                ProfileName = "p",
            }
        );

        messengerMock.Verify(
            m => m.SendToModerators("EncodingStarted", "dashboardHub", It.IsAny<object>()),
            Times.Once
        );

        handler.Dispose();

        await bus.PublishAsync(
            new EncodingStartedEvent
            {
                JobId = 2,
                InputPath = "/b.mkv",
                OutputPath = "/out2/",
                ProfileName = "p2",
            }
        );

        messengerMock.Verify(
            m => m.SendToModerators("EncodingStarted", "dashboardHub", It.IsAny<object>()),
            Times.Once
        );
    }

    [Fact]
    public async Task EncodingStarted_Timestamp_IsPreservedInDto()
    {
        InMemoryEventBus bus = new();
        List<object?> capturedPayloads = [];
        Mock<IClientMessenger> messengerMock = new(MockBehavior.Strict);
        messengerMock
            .Setup(m =>
                m.SendToModerators(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>())
            )
            .Callback<string, string, object?>((_, _, payload) => capturedPayloads.Add(payload))
            .Returns(Task.CompletedTask);
        using SignalREncodingEventHandler handler = new(
            NullLogger<SignalREncodingEventHandler>.Instance,
            bus,
            messengerMock.Object
        );

        EncodingStartedEvent publishedEvent = new()
        {
            JobId = 8,
            InputPath = "/x.mkv",
            OutputPath = "/y/",
            ProfileName = "hevc",
        };

        await bus.PublishAsync(publishedEvent);

        capturedPayloads.Should().ContainSingle();
        EncodingStartedDto capturedDto = capturedPayloads[0]
            .Should()
            .BeOfType<EncodingStartedDto>()
            .Subject;
        capturedDto.Timestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EncodingProgressUpdated_NullableFields_AreMappedCorrectly_WhenAbsent()
    {
        (
            InMemoryEventBus bus,
            Mock<IClientMessenger> messengerMock,
            SignalREncodingEventHandler handler
        ) = BuildChain();
        using SignalREncodingEventHandler _ = handler;

        EncodingProgressUpdatedEvent publishedEvent = new()
        {
            JobId = 20,
            Percentage = 10.0,
            Elapsed = TimeSpan.FromSeconds(5),
        };

        await bus.PublishAsync(publishedEvent);

        messengerMock.Verify(
            m =>
                m.SendToModerators(
                    "EncodingProgress",
                    "dashboardHub",
                    It.Is<EncodingProgressDto>(dto =>
                        dto.Estimated == null
                        && dto.Fps == null
                        && dto.Speed == null
                        && dto.BitrateKbps == null
                    )
                ),
            Times.Once
        );
    }
}
