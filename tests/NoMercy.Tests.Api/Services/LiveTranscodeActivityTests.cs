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

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;
using NoMercy.Api.Controllers.V1.Streaming.Dtos;
using NoMercy.Api.Services;
using NoMercy.Authorization.LiveIngest;
using NoMercy.Data.Activity;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.Users;
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Devices;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.LiveTranscode;
using NoMercy.Storage;
using NoMercyQueue.Core.Resources;
using Xunit;
using DatabaseActivity = NoMercy.Database.Activity;

namespace NoMercy.Tests.Api.Services;

/// <summary>
/// A live transcode is the heaviest thing the server does, so the activity log
/// must say who started it, on which device, and when it ended, the same way
/// it records a device connecting.
/// </summary>
[Trait("Category", "Unit")]
public class LiveTranscodeActivityTests : IDisposable
{
    private const string ClientDeviceId = "client-device-0001";
    private const int MovieId = 424_242;

    [Fact]
    public async Task TranscodeStart_WritesOneEntryNamingUserDeviceAndEncode()
    {
        using Harness harness = new(PlaybackAction.TranscodeVideo);

        LiveResult result = await harness.StartAsync();

        result.Kind.Should().Be(LiveResultKind.Ok);
        string sessionId = ((StartLiveSessionResponse)result.Payload!).SessionId;
        ActivityLog row = harness.Logs().Should().ContainSingle().Subject;
        row.Category.Should().Be(ActivityCategory.Playback);
        row.Type.Should().Be("playback.transcode_started");
        row.UserId.Should().Be(harness.Owner);
        row.DeviceId.Should().Be(harness.DeviceUlid);
        row.MediaId.Should().Be(harness.File.Id);
        JObject meta = JObject.Parse(row.Metadata!);
        meta.Value<string>("session_id").Should().Be(sessionId);
        meta.Value<string>("title").Should().Be("Made Up Movie");
        meta.Value<string>("mode").Should().Be("transcode");
        meta.Value<string>("device_name").Should().Be("Test Browser");
        meta.Value<string>("device_type").Should().Be("web");
        meta.Value<string>("quality").Should().Be("1080p");
        meta.Value<int>("bitrate_kbps").Should().Be(8000);
        meta.Value<string>("encoder").Should().Be("libx264");
        meta.Value<double>("start_seconds").Should().Be(64);
    }

    [Fact]
    public async Task EndSession_WritesASecondEntryWithTheDuration()
    {
        using Harness harness = new(PlaybackAction.TranscodeVideo);
        LiveResult started = await harness.StartAsync();
        string sessionId = ((StartLiveSessionResponse)started.Payload!).SessionId;

        await harness.EndAsync(sessionId);

        List<ActivityLog> rows = harness.Logs();
        rows.Select(r => r.Type)
            .Should()
            .Equal("playback.transcode_started", "playback.transcode_ended");
        ActivityLog ended = rows[1];
        ended.UserId.Should().Be(harness.Owner);
        ended.DeviceId.Should().Be(harness.DeviceUlid);
        JObject.Parse(ended.Metadata!).Value<string>("session_id").Should().Be(sessionId);
        JObject.Parse(ended.Metadata!).Value<double?>("duration_seconds").Should().NotBeNull();
    }

    [Fact]
    public async Task ExpiredSession_WritesTheEndEntryOnce()
    {
        using Harness harness = new(PlaybackAction.TranscodeVideo);
        LiveResult started = await harness.StartAsync();
        string sessionId = ((StartLiveSessionResponse)started.Payload!).SessionId;

        await harness.Streaming.RemoveAsync(sessionId);
        await harness.Streaming.RemoveAsync(sessionId);

        harness.Logs().Count(r => r.Type == "playback.transcode_ended").Should().Be(1);
    }

    [Fact]
    public async Task DirectPlay_WritesNoTranscodeEntry()
    {
        using Harness harness = new(PlaybackAction.DirectPlay);

        LiveResult result = await harness.StartAsync();

        result.Kind.Should().Be(LiveResultKind.Ok);
        harness.Logs().Should().BeEmpty();
    }

    [Fact]
    public async Task Entry_NeverHoldsTheIngestKey()
    {
        using Harness harness = new(PlaybackAction.TranscodeVideo);

        await harness.StartAsync();

        string key = harness.IssuedKey();
        key.Should().NotBeNullOrEmpty();
        harness.Logs().Should().OnlyContain(r => !r.Metadata!.Contains(key));
    }

    [Fact]
    public async Task FailedLogWrite_DoesNotFailTheSession()
    {
        using Harness harness = new(PlaybackAction.TranscodeVideo, logWriteFails: true);

        LiveResult result = await harness.StartAsync();

        result.Kind.Should().Be(LiveResultKind.Ok);
        ((StartLiveSessionResponse)result.Payload!).SessionId.Should().NotBeNullOrEmpty();
    }

    public void Dispose() { }

    private sealed class RecordingIngestKeyStore : ILiveIngestKeyStore
    {
        private readonly LiveIngestKeyStore _inner = new();

        public List<string> Issued { get; } = [];

        public string Issue(string servedPath)
        {
            string key = _inner.Issue(servedPath);
            Issued.Add(key);
            return key;
        }

        public void BindSession(string key, string sessionId) => _inner.BindSession(key, sessionId);

        public bool TryValidate(string key, string requestPath) =>
            _inner.TryValidate(key, requestPath);

        public void RevokeSession(string sessionId) => _inner.RevokeSession(sessionId);

        public void RevokeIfUnbound(string key) => _inner.RevokeIfUnbound(key);
    }

    private sealed class Harness : IDisposable
    {
        private static readonly LiveQuality Quality = new(
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

        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<MediaContext> _options;
        private readonly RecordingIngestKeyStore _keys = new();
        private readonly LiveTranscodeService _service;

        public Guid Owner { get; } = Guid.NewGuid();
        public Ulid DeviceUlid { get; } = Ulid.NewUlid();
        public LiveStreamingService Streaming { get; }
        public VideoFile File { get; } =
            new()
            {
                Share = Ulid.NewUlid().ToString(),
                Folder = "/Movie/",
                Filename = "Made.Up.Movie.mkv",
                HostFolder = "library/Movie",
                MovieId = MovieId,
            };

        public Harness(PlaybackAction decision, bool logWriteFails = false)
        {
            _connection = new("Data Source=:memory:");
            _connection.Open();
            using (SqliteCommand fkOff = _connection.CreateCommand())
            {
                fkOff.CommandText = "PRAGMA foreign_keys = OFF;";
                fkOff.ExecuteNonQuery();
            }
            _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;
            using (MediaContext ctx = new(_options))
            {
                ctx.Database.EnsureCreated();
                ctx.Devices.Add(
                    new Device
                    {
                        Id = DeviceUlid,
                        DeviceId = ClientDeviceId,
                        Type = "web",
                        Name = "Test Browser",
                        OwnerUserId = Owner,
                    }
                );
                ctx.Movies.Add(
                    new Movie
                    {
                        Id = MovieId,
                        Title = "Made Up Movie",
                        TitleSort = "made up movie",
                        LibraryId = Ulid.NewUlid(),
                    }
                );
                ctx.SaveChanges();
            }

            Mock<IDbContextFactory<MediaContext>> factory = new();
            factory
                .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new(_options));

            DatabaseActivity.IActivityLogger logger = logWriteFails
                ? ThrowingLogger()
                : new ActivityLogger(
                    factory.Object,
                    NullLogger<ActivityLogger>.Instance,
                    hubBroadcaster: null
                );
            LiveSessionActivityRecorder recorder = new(
                logger,
                factory.Object,
                NullLogger<LiveSessionActivityRecorder>.Instance
            );

            MediaInfo info = new(
                "library/Movie/Made.Up.Movie.mkv",
                "matroska,webm",
                TimeSpan.FromMinutes(20),
                3000,
                450_000_000,
                [
                    new VideoStreamInfo(0, "h264", 1920, 1080, 24, 8, "yuv420p", null, null, null, true, 2800),
                ],
                [],
                [],
                []
            );

            Mock<IStorage> storage = new();
            storage
                .Setup(s => s.CombinePath(File.HostFolder, File.Filename))
                .Returns(info.FilePath);

            Mock<IVideoFileRepository> files = new();
            files
                .Setup(r =>
                    r.GetForUserWithMetadataAsync(File.Id, Owner, It.IsAny<CancellationToken>())
                )
                .ReturnsAsync(File);
            files
                .Setup(r =>
                    r.IsLibraryFolderAsync(Ulid.Parse(File.Share), It.IsAny<CancellationToken>())
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
                .Returns(Task.FromResult(info));

            Mock<IPlaybackDecisionEngine> decisions = new();
            decisions
                .Setup(d => d.Decide(It.IsAny<MediaInfo>(), It.IsAny<ClientCapabilities>()))
                .Returns(new PlaybackDecision(decision, "test fixture", null));

            Mock<IDeviceAwareVariantSelector> variants = new();
            variants
                .Setup(v => v.ApplyDeviceCaps(It.IsAny<ClientCapabilities>(), It.IsAny<DeviceCapabilities?>()))
                .Returns((ClientCapabilities caps, DeviceCapabilities? _) => caps);

            SessionManager sessions = new(new LiveSessionLimits());
            Streaming = new(
                NullLogger<LiveStreamingService>.Instance,
                storage.Object,
                Mock.Of<ILiveSegmentInventory>(),
                sessionManager: sessions,
                endObserver: recorder
            );

            Mock<ILiveEncoder> encoder = new();
            encoder
                .Setup(e => e.StartAsync(It.IsAny<LiveEncodeRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(
                    (LiveEncodeRequest request, CancellationToken _) =>
                    {
                        LiveSession session = new("video", Quality);
                        Streaming.Register(session, TimeSpan.FromSeconds(6));
                        Streaming.StampRequestContext(session.SessionId, info, request.Client);
                        return session;
                    }
                );

            _service = new(
                encoder.Object,
                Streaming,
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
                NullLogger<LiveTranscodeService>.Instance,
                activity: recorder
            );
        }

        private static DatabaseActivity.IActivityLogger ThrowingLogger()
        {
            Mock<DatabaseActivity.IActivityLogger> mock = new();
            mock.Setup(l =>
                    l.LogPlaybackAsync(
                        It.IsAny<string>(),
                        It.IsAny<Guid>(),
                        It.IsAny<Ulid>(),
                        It.IsAny<Ulid>(),
                        It.IsAny<object?>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .ThrowsAsync(new InvalidOperationException("database is down"));
            return mock.Object;
        }

        public Task<LiveResult> StartAsync() =>
            _service.StartSessionAsync(
                Owner,
                new StartLiveSessionRequest(
                    File.Id.ToString(),
                    new ClientCapabilitiesDto(null, null, ["mp4"], false, 10000),
                    64,
                    null
                ),
                ClientDeviceId,
                CancellationToken.None
            );

        public Task<LiveResult> EndAsync(string sessionId) =>
            _service.EndSessionAsync(Owner, sessionId, CancellationToken.None);

        public List<ActivityLog> Logs()
        {
            using MediaContext ctx = new(_options);
            return [.. ctx.ActivityLogs.AsNoTracking().OrderBy(l => l.Id)];
        }

        public string IssuedKey() => _keys.Issued.FirstOrDefault() ?? string.Empty;

        public void Dispose() => _connection.Dispose();
    }
}
