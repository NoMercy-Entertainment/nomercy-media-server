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

using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Api.Hubs;
using NoMercy.Data.Activity;
using NoMercy.Database;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.LiveTranscode;
using NoMercy.Networking.Messaging;
using Xunit;

namespace NoMercy.Tests.Api;

// Regression coverage for the live-transcode buffer-ahead fix: BufferAhead used
// to be driven entirely by the segment-request-derived prefetch frontier, which
// tracks how far the player has prefetched rather than where the user is
// actually watching. ReportPlayhead lets a live client report its true
// position so the 30s over-buffer suspend engages on the real watch position.
// These tests build a real LiveTranscodeHub against real ISessionManager /
// ILiveStreamingService mocks and a real LiveSession/LiveRuntimeSession pair,
// mocking only the SignalR plumbing (HubCallerContext) a live connection would
// normally supply.
[Trait("Category", "Unit")]
public class LiveTranscodeHubReportPlayheadTests
{
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

    private static LiveTranscodeHub CreateHub(
        ISessionManager sessionManager,
        ILiveStreamingService streamingService,
        string callerUserId
    )
    {
        DefaultHttpContext httpContext = new() { RequestServices = null! };
        httpContext.Request.Path = "/liveTranscodeHub";

        LiveTranscodeHub hub = new(
            new HttpContextAccessorStub(httpContext),
            Mock.Of<IDbContextFactory<MediaContext>>(),
            new ConnectedClients(),
            Mock.Of<IActivityLogger>(),
            sessionManager,
            streamingService,
            Mock.Of<ILiveSessionPresenceTracker>(),
            NullLogger<LiveTranscodeHub>.Instance
        );

        ClaimsPrincipal principal = new(
            new ClaimsIdentity([new(ClaimTypes.NameIdentifier, callerUserId)], "TestAuth")
        );

        Mock<HubCallerContext> context = new();
        context.Setup(c => c.User).Returns(principal);
        context.Setup(c => c.UserIdentifier).Returns(callerUserId);
        context.Setup(c => c.ConnectionId).Returns(Guid.NewGuid().ToString());
        context.Setup(c => c.ConnectionAborted).Returns(CancellationToken.None);

        Mock<IHubCallerClients> clients = new();
        hub.Context = context.Object;
        hub.Clients = clients.Object;

        return hub;
    }

    [Fact]
    public async Task ReportPlayhead_ByOwner_UpdatesPlaybackPosition_AndTouchesLastAccess()
    {
        const string sessionId = "sess-owner";
        const string ownerId = "user-1";

        LiveSession session = new(sessionId, MakeQuality());
        // SeekAsync sets TranscodedPosition to the target too (PushSegment is
        // internal and not visible from this test assembly), giving a known
        // 60s TranscodedPosition to measure BufferAhead against.
        await session.SeekAsync(TimeSpan.FromSeconds(60), CancellationToken.None);
        LiveRuntimeSession runtime = new(session, TimeSpan.FromSeconds(6));
        DateTime lastAccessBeforeCall = runtime.LastAccess;

        Mock<ISessionManager> sessionManager = new();
        sessionManager.Setup(m => m.GetOwnerUserId(sessionId)).Returns(ownerId);

        Mock<ILiveStreamingService> streamingService = new();
        streamingService.Setup(s => s.TryGetRuntime(sessionId, out runtime)).Returns(true);

        LiveTranscodeHub hub = CreateHub(sessionManager.Object, streamingService.Object, ownerId);

        HubCommandResult result = hub.ReportPlayhead(sessionId, 42.5);

        result.Ok.Should().BeTrue();
        // TranscodedPosition is 60s (from the pushed segment); the reported
        // playhead of 42.5s must be applied authoritatively.
        session.BufferAhead.Should().Be(TimeSpan.FromSeconds(17.5));
        runtime.LastAccess.Should().BeOnOrAfter(lastAccessBeforeCall);
    }

    [Fact]
    public async Task ReportPlayhead_NegativeSeconds_ReturnsInvalidInput()
    {
        const string sessionId = "sess-clamp";
        const string ownerId = "user-1";

        LiveSession session = new(sessionId, MakeQuality());
        await session.SeekAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        LiveRuntimeSession runtime = new(session, TimeSpan.FromSeconds(6));
        TimeSpan bufferAheadBeforeCall = session.BufferAhead;

        Mock<ISessionManager> sessionManager = new();
        sessionManager.Setup(m => m.GetOwnerUserId(sessionId)).Returns(ownerId);

        Mock<ILiveStreamingService> streamingService = new();
        streamingService.Setup(s => s.TryGetRuntime(sessionId, out runtime)).Returns(true);

        LiveTranscodeHub hub = CreateHub(sessionManager.Object, streamingService.Object, ownerId);

        HubCommandResult result = hub.ReportPlayhead(sessionId, -5);

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be("invalid_input");
        session.BufferAhead.Should().Be(bufferAheadBeforeCall);
    }

    [Fact]
    public async Task ReportPlayhead_ByNonOwner_IsRejected()
    {
        const string sessionId = "sess-owner";
        const string ownerId = "user-1";
        const string callerId = "user-2";

        LiveSession session = new(sessionId, MakeQuality());
        await session.SeekAsync(TimeSpan.FromSeconds(60), CancellationToken.None);
        LiveRuntimeSession runtime = new(session, TimeSpan.FromSeconds(6));
        DateTime lastAccessBeforeCall = runtime.LastAccess;
        TimeSpan bufferAheadBeforeCall = session.BufferAhead;

        Mock<ISessionManager> sessionManager = new();
        sessionManager.Setup(m => m.GetOwnerUserId(sessionId)).Returns(ownerId);

        Mock<ILiveStreamingService> streamingService = new();
        streamingService.Setup(s => s.TryGetRuntime(sessionId, out runtime)).Returns(true);

        LiveTranscodeHub hub = CreateHub(sessionManager.Object, streamingService.Object, callerId);

        HubCommandResult result = hub.ReportPlayhead(sessionId, 42.5);

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be("forbidden");
        session.BufferAhead.Should().Be(bufferAheadBeforeCall);
        runtime.LastAccess.Should().Be(lastAccessBeforeCall);
    }

    [Fact]
    public void ReportPlayhead_UnknownSession_IsNoOp()
    {
        Mock<ISessionManager> sessionManager = new();
        Mock<ILiveStreamingService> streamingService = new();
        // No Setup for TryGetRuntime — unconfigured returns false with a null
        // out value, mirroring an unknown/expired session id.

        LiveTranscodeHub hub = CreateHub(sessionManager.Object, streamingService.Object, "user-1");

        HubCommandResult result = hub.ReportPlayhead("sess-unknown", 10);

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be("not_found");
        // Mirrors Heartbeat's order: TryGetRuntime is checked before the owner
        // lookup, so an unknown session never reaches GetOwnerUserId.
        sessionManager.Verify(m => m.GetOwnerUserId(It.IsAny<string>()), Times.Never);
    }

    private sealed class HttpContextAccessorStub(HttpContext httpContext) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = httpContext;
    }
}
