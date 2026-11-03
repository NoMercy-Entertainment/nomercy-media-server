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
using NoMercy.Database.Models.Media;
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
/// The per-user and global live-session caps bound how many encoders run. A
/// start spends seconds between its cap check and registering its session (the
/// probe, the encoder spawn), so the slot must be claimed at the check: parallel
/// starts for one user may not each see the same free slot, and a start that
/// ends without a session must hand its slot back.
/// </summary>
[Trait("Category", "Unit")]
public class LiveTranscodeSessionCapTests
{
    [Fact]
    public async Task ConcurrentStarts_ForOneUserAtThePerUserCap_StartExactlyOneEncoder()
    {
        Harness harness = new(maxSessionsPerUser: 1);

        Task<LiveResult> first = harness.StartAsync();
        Task<LiveResult> second = harness.StartAsync();
        harness.ReleaseEncoder();
        LiveResult[] results = await Task.WhenAll(first, second);

        results.Count(r => r.Kind == LiveResultKind.Ok).Should().Be(1);
        LiveResult refused = results.Single(r => r.Kind != LiveResultKind.Ok);
        refused.Kind.Should().Be(LiveResultKind.ServiceUnavailable);
        refused.Message.Should().Be("Maximum concurrent live sessions reached");
        harness.EncoderStarts.Should().Be(1);
        harness.Sessions.ActiveSessionCount.Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentStarts_ByDifferentUsersAtTheGlobalCap_StartExactlyOneEncoder()
    {
        Harness harness = new(maxSessionsPerUser: 2, maxConcurrentSessions: 1);

        Task<LiveResult> first = harness.StartAsync();
        Task<LiveResult> second = harness.StartAsync(Guid.NewGuid());
        harness.ReleaseEncoder();
        LiveResult[] results = await Task.WhenAll(first, second);

        results.Count(r => r.Kind == LiveResultKind.Ok).Should().Be(1);
        harness.EncoderStarts.Should().Be(1);
        harness.Sessions.ActiveSessionCount.Should().Be(1);
    }

    [Fact]
    public async Task FailedEncoderStart_HandsTheSlotBack_SoTheNextStartSucceeds()
    {
        Harness harness = new(maxSessionsPerUser: 1, encoderFailsOnFirstStart: true);
        harness.ReleaseEncoder();

        LiveResult failed = await harness.StartAsync();
        LiveResult retried = await harness.StartAsync();

        failed.Kind.Should().Be(LiveResultKind.ServiceUnavailable);
        failed.Message.Should().Be("no encoder slot");
        retried.Kind.Should().Be(LiveResultKind.Ok);
        harness.EncoderStarts.Should().Be(2);
        harness.Sessions.ActiveSessionCount.Should().Be(1);
    }

    [Fact]
    public async Task FailedProbe_HandsTheSlotBack_SoTheNextStartSucceeds()
    {
        Harness harness = new(maxSessionsPerUser: 1, analyzeFailsOnFirstStart: true);
        harness.ReleaseEncoder();

        LiveResult fallback = await harness.StartAsync();
        LiveResult retried = await harness.StartAsync();

        fallback.Kind.Should().Be(LiveResultKind.Ok);
        ((StartLiveSessionResponse)fallback.Payload!).Mode.Should().Be("direct");
        retried.Kind.Should().Be(LiveResultKind.Ok);
        ((StartLiveSessionResponse)retried.Payload!).Mode.Should().NotBe("direct");
        harness.EncoderStarts.Should().Be(1);
        harness.Sessions.ActiveSessionCount.Should().Be(1);
    }

    [Fact]
    public async Task SecondStart_WhenTheFirstSessionIsRegistered_EvictsTheStalestAndSucceeds()
    {
        Harness harness = new(maxSessionsPerUser: 1);
        harness.ReleaseEncoder();

        LiveResult first = await harness.StartAsync();
        LiveResult second = await harness.StartAsync();

        first.Kind.Should().Be(LiveResultKind.Ok);
        second.Kind.Should().Be(LiveResultKind.Ok);
        harness.EncoderStarts.Should().Be(2);
        harness.Sessions.ActiveSessionCount.Should().Be(1);
        harness
            .Sessions.GetUserSessionIds(harness.Owner.ToString())
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(((StartLiveSessionResponse)second.Payload!).SessionId);
    }

    private sealed class Harness
    {
        private static readonly LiveQuality Quality = new(
            Id: "1080p",
            Label: "1080p",
            Width: 1920,
            Height: 1080,
            Codec: VideoCodecType.H264,
            BitrateKbps: 3000,
            Encoder: "libx264",
            IsHardwareAccelerated: false,
            ExpectedSpeed: 2.0,
            CanRealtime: true
        );

        // Every encoder start parks here until the test lets it go, so the test
        // decides when the first start is "inside the encoder" and the second
        // start must already have been judged against the cap.
        private readonly TaskCompletionSource _encoderGate = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        private readonly LiveTranscodeService _service;
        private int _encoderStarts;
        private int _analyzeCalls;

        // A non-library file (its share is not a folder ULID), so the start reads
        // the source straight off the filesystem and mints no ingest key.
        private readonly VideoFile _file = new()
        {
            Share = "disc",
            Folder = "/Show/Ep/",
            Filename = "Ep.mkv",
            HostFolder = "disc/Show/Ep",
        };

        public Guid Owner { get; } = Guid.NewGuid();

        public SessionManager Sessions { get; }

        public int EncoderStarts => Volatile.Read(ref _encoderStarts);

        public Harness(
            int maxSessionsPerUser,
            int maxConcurrentSessions = 4,
            bool encoderFailsOnFirstStart = false,
            bool analyzeFailsOnFirstStart = false
        )
        {
            MediaInfo info = new(
                "disc/Show/Ep/Ep.mkv",
                "matroska,webm",
                TimeSpan.FromMinutes(20),
                3000,
                450_000_000,
                [
                    new VideoStreamInfo(
                        0,
                        "h264",
                        1920,
                        1080,
                        24,
                        8,
                        "yuv420p",
                        null,
                        null,
                        null,
                        true,
                        2800
                    ),
                ],
                [],
                [],
                []
            );

            Mock<IStorage> storage = new();
            storage
                .Setup(s => s.CombinePath(_file.HostFolder, _file.Filename))
                .Returns(info.FilePath);

            Mock<IVideoFileRepository> files = new();
            files
                .Setup(r =>
                    r.GetForUserWithMetadataAsync(
                        _file.Id,
                        It.IsAny<Guid>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .ReturnsAsync(_file);

            Mock<IMediaAnalyzer> analyzer = new();
            analyzer
                .Setup(a =>
                    a.AnalyzeAsync(
                        It.IsAny<string>(),
                        It.IsAny<string[]?>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .Returns(() =>
                    analyzeFailsOnFirstStart && Interlocked.Increment(ref _analyzeCalls) == 1
                        ? Task.FromException<MediaInfo>(new IOException("probe failed"))
                        : Task.FromResult(info)
                );

            Mock<IPlaybackDecisionEngine> decisions = new();
            decisions
                .Setup(d => d.Decide(It.IsAny<MediaInfo>(), It.IsAny<ClientCapabilities>()))
                .Returns(new PlaybackDecision(PlaybackAction.TranscodeVideo, "test fixture", null));

            Mock<IDeviceAwareVariantSelector> variants = new();
            variants
                .Setup(v => v.ApplyDeviceCaps(It.IsAny<ClientCapabilities>(), null))
                .Returns((ClientCapabilities caps, DeviceCapabilities? _) => caps);

            LiveSessionLimits limits = new()
            {
                MaxConcurrentSessions = maxConcurrentSessions,
                MaxSessionsPerUser = maxSessionsPerUser,
            };
            Sessions = new(limits);
            LiveStreamingService streaming = new(
                NullLogger<LiveStreamingService>.Instance,
                storage.Object,
                Mock.Of<ILiveSegmentInventory>(),
                sessionManager: Sessions
            );

            Mock<ILiveEncoder> encoder = new();
            encoder
                .Setup(e =>
                    e.StartAsync(It.IsAny<LiveEncodeRequest>(), It.IsAny<CancellationToken>())
                )
                .Returns(
                    async (LiveEncodeRequest request, CancellationToken _) =>
                    {
                        int start = Interlocked.Increment(ref _encoderStarts);
                        await _encoderGate.Task;

                        if (encoderFailsOnFirstStart && start == 1)
                            throw new InvalidOperationException("no encoder slot");

                        LiveSession session = new(Ulid.NewUlid().ToString(), Quality);
                        streaming.Register(session, TimeSpan.FromSeconds(6));
                        streaming.StampRequestContext(session.SessionId, info, request.Client);
                        return (ILiveSession)session;
                    }
                );

            _service = new(
                encoder.Object,
                streaming,
                new LivePlaylistBuilder(),
                Sessions,
                analyzer.Object,
                Mock.Of<ILiveQualitySelector>(),
                decisions.Object,
                Mock.Of<ISpeedIndexProvider>(p =>
                    p.Current == new SpeedIndex(new Dictionary<SpeedKey, SpeedMeasurement>())
                ),
                Mock.Of<IResourceBudget>(),
                files.Object,
                limits,
                storage.Object,
                Mock.Of<IDeviceCapabilityRegistry>(),
                variants.Object,
                Mock.Of<ILiveIngestKeyStore>(),
                NullLogger<LiveTranscodeService>.Instance
            );
        }

        public void ReleaseEncoder() => _encoderGate.TrySetResult();

        public Task<LiveResult> StartAsync(Guid? userId = null) =>
            _service.StartSessionAsync(
                userId ?? Owner,
                new StartLiveSessionRequest(
                    _file.Id.ToString(),
                    new ClientCapabilitiesDto(null, null, ["mp4"], false, 10000),
                    0,
                    null
                ),
                null,
                CancellationToken.None
            );
    }
}
