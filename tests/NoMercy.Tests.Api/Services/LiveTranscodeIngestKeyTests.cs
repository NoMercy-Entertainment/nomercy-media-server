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
/// A library file's self-ingest key is minted before a start knows whether a
/// transcode will run, and only a key bound to a session dies with that session.
/// Every start that ends without a session must therefore drop its own key, or
/// the key unlocks the file's folder for its whole absolute lifetime.
/// </summary>
[Trait("Category", "Unit")]
public class LiveTranscodeIngestKeyTests
{
    [Fact]
    public async Task DirectPlay_RevokesTheIngestKey()
    {
        Harness harness = new(PlaybackAction.DirectPlay);

        LiveResult result = await harness.StartAsync();

        result.Kind.Should().Be(LiveResultKind.Ok);
        ((StartLiveSessionResponse)result.Payload!).Mode.Should().Be("direct");
        harness.IssuedKeyIsValid().Should().BeFalse();
    }

    [Fact]
    public async Task AnalyzeFailure_RevokesTheIngestKey()
    {
        Harness harness = new(PlaybackAction.TranscodeVideo, analyzeFails: true);

        LiveResult result = await harness.StartAsync();

        result.Kind.Should().Be(LiveResultKind.Ok);
        ((StartLiveSessionResponse)result.Payload!).Mode.Should().Be("direct");
        harness.IssuedKeyIsValid().Should().BeFalse();
    }

    [Fact]
    public async Task EncoderStartFailure_RevokesTheIngestKey()
    {
        Harness harness = new(PlaybackAction.TranscodeVideo, encoderFails: true);

        LiveResult result = await harness.StartAsync();

        result.Kind.Should().Be(LiveResultKind.ServiceUnavailable);
        harness.IssuedKeyIsValid().Should().BeFalse();
    }

    [Fact]
    public async Task StartedSession_KeepsTheKeyUntilTheSessionEnds()
    {
        Harness harness = new(PlaybackAction.TranscodeVideo);

        LiveResult started = await harness.StartAsync();

        started.Kind.Should().Be(LiveResultKind.Ok);
        harness.IssuedKeyIsValid().Should().BeTrue();

        string sessionId = ((StartLiveSessionResponse)started.Payload!).SessionId;
        LiveResult ended = await harness.EndAsync(sessionId);

        ended.Kind.Should().Be(LiveResultKind.Ok);
        harness.IssuedKeyIsValid().Should().BeFalse();
    }

    // The real store, remembering what it minted so a test can ask whether the
    // one key this start issued still unlocks the file it was issued for.
    private sealed class RecordingIngestKeyStore : ILiveIngestKeyStore
    {
        private readonly LiveIngestKeyStore _inner = new();

        public List<(string Key, string ServedPath)> Issued { get; } = [];

        public string Issue(string servedPath)
        {
            string key = _inner.Issue(servedPath);
            Issued.Add((key, servedPath));
            return key;
        }

        public void BindSession(string key, string sessionId) => _inner.BindSession(key, sessionId);

        public bool TryValidate(string key, string requestPath) =>
            _inner.TryValidate(key, requestPath);

        public void RevokeSession(string sessionId) => _inner.RevokeSession(sessionId);

        public void RevokeIfUnbound(string key) => _inner.RevokeIfUnbound(key);
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

        private readonly Guid _owner = Guid.NewGuid();
        private readonly RecordingIngestKeyStore _keys = new();
        private readonly LiveTranscodeService _service;

        // A library file: its share is a folder ULID the repository confirms, so
        // the start self-ingests over HTTP and mints a key for it.
        private readonly VideoFile _file = new()
        {
            Share = Ulid.NewUlid().ToString(),
            Folder = "/Show/Ep/",
            Filename = "Ep.mkv",
            HostFolder = "library/Show/Ep",
        };

        public Harness(
            PlaybackAction decision,
            bool analyzeFails = false,
            bool encoderFails = false
        )
        {
            MediaInfo info = new(
                "library/Show/Ep/Ep.mkv",
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
                    r.GetForUserWithMetadataAsync(_file.Id, _owner, It.IsAny<CancellationToken>())
                )
                .ReturnsAsync(_file);
            files
                .Setup(r =>
                    r.IsLibraryFolderAsync(Ulid.Parse(_file.Share), It.IsAny<CancellationToken>())
                )
                .ReturnsAsync(true);

            Mock<IMediaAnalyzer> analyzer = new();
            analyzer
                .Setup(a =>
                    a.AnalyzeAsync(
                        It.IsAny<string>(),
                        It.IsAny<string[]?>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .Returns(
                    analyzeFails
                        ? Task.FromException<MediaInfo>(new IOException("probe failed"))
                        : Task.FromResult(info)
                );

            Mock<IPlaybackDecisionEngine> decisions = new();
            decisions
                .Setup(d => d.Decide(It.IsAny<MediaInfo>(), It.IsAny<ClientCapabilities>()))
                .Returns(new PlaybackDecision(decision, "test fixture", null));

            Mock<IDeviceAwareVariantSelector> variants = new();
            variants
                .Setup(v => v.ApplyDeviceCaps(It.IsAny<ClientCapabilities>(), null))
                .Returns((ClientCapabilities caps, DeviceCapabilities? _) => caps);

            SessionManager sessions = new(new LiveSessionLimits());
            LiveStreamingService streaming = new(
                NullLogger<LiveStreamingService>.Instance,
                storage.Object,
                Mock.Of<ILiveSegmentInventory>(),
                sessionManager: sessions
            );

            Mock<ILiveEncoder> encoder = new();
            if (encoderFails)
                encoder
                    .Setup(e =>
                        e.StartAsync(It.IsAny<LiveEncodeRequest>(), It.IsAny<CancellationToken>())
                    )
                    .ThrowsAsync(new InvalidOperationException("no encoder slot"));
            else
                encoder
                    .Setup(e =>
                        e.StartAsync(It.IsAny<LiveEncodeRequest>(), It.IsAny<CancellationToken>())
                    )
                    .ReturnsAsync(
                        (LiveEncodeRequest request, CancellationToken _) =>
                        {
                            LiveSession session = new("video", Quality);
                            streaming.Register(session, TimeSpan.FromSeconds(6));
                            streaming.StampRequestContext(session.SessionId, info, request.Client);
                            return session;
                        }
                    );

            _service = new(
                encoder.Object,
                streaming,
                new LivePlaylistBuilder(),
                sessions,
                analyzer.Object,
                Mock.Of<ILiveQualitySelector>(),
                decisions.Object,
                new SpeedIndex([]),
                Mock.Of<IResourceBudget>(),
                files.Object,
                new LiveSessionLimits(),
                storage.Object,
                Mock.Of<IDeviceCapabilityRegistry>(),
                variants.Object,
                _keys,
                NullLogger<LiveTranscodeService>.Instance
            );
        }

        public Task<LiveResult> StartAsync() =>
            _service.StartSessionAsync(
                _owner,
                new StartLiveSessionRequest(
                    _file.Id.ToString(),
                    new ClientCapabilitiesDto(null, null, ["mp4"], false, 10000),
                    0,
                    null
                ),
                null,
                CancellationToken.None
            );

        public Task<LiveResult> EndAsync(string sessionId) =>
            _service.EndSessionAsync(_owner, sessionId, CancellationToken.None);

        public bool IssuedKeyIsValid()
        {
            (string key, string servedPath) = _keys.Issued.Should().ContainSingle().Subject;
            return _keys.TryValidate(key, servedPath);
        }
    }
}
