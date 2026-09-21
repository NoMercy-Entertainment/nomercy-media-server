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
using NoMercy.Api.Controllers.V1.Streaming.Dtos;
using NoMercy.Api.Services;
using NoMercy.Authorization.LiveIngest;
using NoMercy.Data.Repositories;
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Devices;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.LiveTranscode;
using NoMercy.Storage;
using NoMercyQueue.Core.Resources;
using Xunit;

namespace NoMercy.Tests.Api.Services;

/// <summary>
/// Every session-scoped REST action must belong to the caller. A session another
/// user started reads as absent, the same as one that never existed, so a leaked
/// or enumerated id reveals nothing and cannot be watched, steered or killed.
/// The SignalR hub already applied this rule; these tests pin it on the REST side.
/// </summary>
[Trait("Category", "Unit")]
public class LiveTranscodeServiceOwnershipTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Stranger = Guid.NewGuid();
    private const string SessionId = "sess-owned";

    private static LiveQuality MakeQuality() =>
        new(
            Id: "1080p",
            Label: "1080p",
            Width: 1920,
            Height: 1080,
            Codec: VideoCodecType.H264,
            BitrateKbps: 8000,
            Encoder: "libx264",
            IsHardwareAccelerated: false,
            ExpectedSpeed: 2.0,
            CanRealtime: true
        );

    private static (
        LiveTranscodeService Service,
        Mock<ILiveStreamingService> Streaming,
        LiveRuntimeSession Runtime
    ) Build()
    {
        LiveSession session = new(SessionId, MakeQuality());
        LiveRuntimeSession runtime = new(session, TimeSpan.FromSeconds(6));

        Mock<ISessionManager> sessionManager = new();
        sessionManager.Setup(m => m.GetOwnerUserId(SessionId)).Returns(Owner.ToString());

        Mock<ILiveStreamingService> streaming = new();
        streaming.Setup(s => s.TryGetRuntime(SessionId, out runtime)).Returns(true);
        streaming.Setup(s => s.WasRecentlyRemoved(SessionId)).Returns(false);
        streaming
            .Setup(s => s.GetActiveSessions())
            .Returns([
                new LiveSessionSnapshot(
                    SessionId: SessionId,
                    State: LiveSessionState.Transcoding,
                    QualityId: "1080p",
                    QualityLabel: "1080p",
                    Width: 1920,
                    Height: 1080,
                    BitrateKbps: 8000,
                    PositionSeconds: 0,
                    BufferAheadSeconds: 0,
                    SegmentCount: 0,
                    IsComplete: false,
                    LastAccess: DateTime.UtcNow
                ),
            ]);

        LiveTranscodeService service = new(
            Mock.Of<ILiveEncoder>(),
            streaming.Object,
            Mock.Of<ILivePlaylistBuilder>(),
            sessionManager.Object,
            Mock.Of<IMediaAnalyzer>(),
            Mock.Of<ILiveQualitySelector>(),
            Mock.Of<IPlaybackDecisionEngine>(),
            new SpeedIndex([]),
            Mock.Of<IResourceBudget>(),
            Mock.Of<IVideoFileRepository>(),
            new LiveSessionLimits(),
            Mock.Of<IStorage>(),
            Mock.Of<IDeviceCapabilityRegistry>(),
            Mock.Of<IDeviceAwareVariantSelector>(),
            Mock.Of<ILiveIngestKeyStore>(),
            NullLogger<LiveTranscodeService>.Instance
        );

        return (service, streaming, runtime);
    }

    [Fact]
    public void ReportPosition_ByStranger_ReadsAsNotFound_AndTouchesNothing()
    {
        (LiveTranscodeService service, _, LiveRuntimeSession runtime) = Build();
        DateTime lastAccessBefore = runtime.LastAccess;

        LiveResult result = service.ReportPosition(
            Stranger,
            SessionId,
            new ReportPositionRequest(TimeSeconds: 42)
        );

        result.Kind.Should().Be(LiveResultKind.NotFound);
        runtime.LastAccess.Should().Be(lastAccessBefore);
    }

    [Fact]
    public void ReportPosition_ByOwner_Succeeds()
    {
        (LiveTranscodeService service, _, _) = Build();

        LiveResult result = service.ReportPosition(
            Owner,
            SessionId,
            new ReportPositionRequest(TimeSeconds: 42)
        );

        result.Kind.Should().Be(LiveResultKind.Ok);
    }

    [Fact]
    public void GetPlaylist_ByStranger_ReadsAsNotFound()
    {
        (LiveTranscodeService service, _, _) = Build();

        LiveResult result = service.GetPlaylist(Stranger, SessionId);

        result.Kind.Should().Be(LiveResultKind.NotFound);
    }

    [Fact]
    public void GetMasterPlaylist_ByStranger_ReadsAsNotFound()
    {
        (LiveTranscodeService service, _, _) = Build();

        LiveResult result = service.GetMasterPlaylist(Stranger, SessionId);

        result.Kind.Should().Be(LiveResultKind.NotFound);
    }

    [Fact]
    public async Task GetSegment_ByStranger_ReadsAsNotFound()
    {
        (LiveTranscodeService service, _, _) = Build();

        LiveResult result = await service.GetSegmentAsync(
            Stranger,
            SessionId,
            "0",
            0,
            CancellationToken.None
        );

        result.Kind.Should().Be(LiveResultKind.NotFound);
    }

    [Fact]
    public void ReportBufferHealth_ByStranger_ReadsAsNotFound()
    {
        (LiveTranscodeService service, _, _) = Build();

        LiveResult result = service.ReportBufferHealth(
            Stranger,
            SessionId,
            new ReportBufferHealthRequest(BufferedSeconds: 10, ObservedBandwidthKbps: 5000)
        );

        result.Kind.Should().Be(LiveResultKind.NotFound);
    }

    [Fact]
    public async Task ChangeQuality_ByStranger_ReadsAsNotFound()
    {
        (LiveTranscodeService service, _, _) = Build();

        LiveResult result = await service.ChangeQualityAsync(
            Stranger,
            SessionId,
            new ChangeQualityRequest(QualityId: "720p"),
            CancellationToken.None
        );

        result.Kind.Should().Be(LiveResultKind.NotFound);
    }

    [Fact]
    public async Task Seek_ByStranger_ReadsAsNotFound()
    {
        (LiveTranscodeService service, _, _) = Build();

        LiveResult result = await service.SeekAsync(
            Stranger,
            SessionId,
            new SeekRequest(PositionSeconds: 30),
            CancellationToken.None
        );

        result.Kind.Should().Be(LiveResultKind.NotFound);
    }

    [Fact]
    public async Task EndSession_ByStranger_ReadsAsNotFound_AndDoesNotRemove()
    {
        (LiveTranscodeService service, Mock<ILiveStreamingService> streaming, _) = Build();

        LiveResult result = await service.EndSessionAsync(
            Stranger,
            SessionId,
            CancellationToken.None
        );

        result.Kind.Should().Be(LiveResultKind.NotFound);
        streaming.Verify(s => s.RemoveAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task EndSession_ByOwner_RemovesTheSession()
    {
        (LiveTranscodeService service, Mock<ILiveStreamingService> streaming, _) = Build();

        LiveResult result = await service.EndSessionAsync(Owner, SessionId, CancellationToken.None);

        result.Kind.Should().Be(LiveResultKind.Ok);
        streaming.Verify(s => s.RemoveAsync(SessionId), Times.Once);
    }

    [Fact]
    public void ListSessions_ForStranger_HidesOtherUsersSessions()
    {
        (LiveTranscodeService service, _, _) = Build();

        IReadOnlyList<LiveSessionDto> sessions = service.ListSessions(Stranger, includeAll: false);

        sessions.Should().BeEmpty();
    }

    [Fact]
    public void ListSessions_ForOwner_ShowsOwnSession()
    {
        (LiveTranscodeService service, _, _) = Build();

        IReadOnlyList<LiveSessionDto> sessions = service.ListSessions(Owner, includeAll: false);

        sessions.Should().ContainSingle(s => s.SessionId == SessionId);
    }

    [Fact]
    public void ListSessions_WithIncludeAll_ShowsEveryone()
    {
        (LiveTranscodeService service, _, _) = Build();

        IReadOnlyList<LiveSessionDto> sessions = service.ListSessions(Stranger, includeAll: true);

        sessions.Should().ContainSingle(s => s.SessionId == SessionId);
    }
}
