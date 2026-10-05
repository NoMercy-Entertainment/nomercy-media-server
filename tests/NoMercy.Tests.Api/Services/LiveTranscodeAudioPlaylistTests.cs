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
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Api.Controllers.V1.Streaming;
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

[Trait("Category", "Unit")]
public class LiveTranscodeAudioPlaylistTests
{
    [Theory]
    [InlineData(PlaybackAction.Remux)]
    [InlineData(PlaybackAction.TranscodeVideo)]
    public async Task Mp4Master_AllRenditionPlaylistsResolveForOwner(PlaybackAction action)
    {
        Guid owner = Guid.NewGuid();
        Guid stranger = Guid.NewGuid();
        VideoFile file = new() { Filename = "movie.mp4", HostFolder = "movies" };
        MediaInfo info = new(
            "movies/movie.mp4",
            "mov,mp4,m4a,3gp,3g2,mj2",
            TimeSpan.FromSeconds(12),
            3000,
            4_500_000,
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
            [
                new AudioStreamInfo(1, "aac", 2, 48000, 128, "eng", true, false),
                new AudioStreamInfo(2, "aac", 2, 48000, 128, "nld", false, false),
            ],
            [],
            []
        );
        LiveQuality quality = new(
            "1080p",
            "1080p",
            1920,
            1080,
            VideoCodecType.H264,
            3000,
            "libx264",
            false,
            2,
            true
        );
        SessionManager sessions = new(new LiveSessionLimits());
        Mock<IStorage> storage = new();
        Mock<ILiveSegmentInventory> inventory = new();
        await using LiveStreamingService streaming = new(
            NullLogger<LiveStreamingService>.Instance,
            storage.Object,
            inventory.Object,
            sessionManager: sessions
        );
        Mock<ILiveEncoder> encoder = new();
        encoder
            .Setup(e => e.StartAsync(It.IsAny<LiveEncodeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                (LiveEncodeRequest request, CancellationToken _) =>
                {
                    LiveSession parent = new("video", quality);
                    streaming.Register(parent, TimeSpan.FromSeconds(6));
                    streaming.StampRequestContext(parent.SessionId, info, request.Client);
                    return parent;
                }
            );
        encoder
            .Setup(e =>
                e.StartAudioRenditionAsync(
                    It.IsAny<LiveEncodeRequest>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                (LiveEncodeRequest request, CancellationToken _) =>
                {
                    LiveSession child = new($"audio-{request.AudioStreamIndex}", quality);
                    streaming.Register(child, TimeSpan.FromSeconds(6), isAudioRenditionChild: true);
                    streaming.StampRequestContext(child.SessionId, info, request.Client);
                    return child;
                }
            );
        Mock<IVideoFileRepository> files = new();
        files
            .Setup(r =>
                r.GetForUserWithMetadataAsync(file.Id, owner, It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(file);
        Mock<IMediaAnalyzer> analyzer = new();
        analyzer
            .Setup(a =>
                a.AnalyzeAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(info);
        Mock<IPlaybackDecisionEngine> decisions = new();
        decisions
            .Setup(d => d.Decide(It.IsAny<MediaInfo>(), It.IsAny<ClientCapabilities>()))
            .Returns(new PlaybackDecision(action, "test fixture", null));
        Mock<IDeviceAwareVariantSelector> variants = new();
        variants
            .Setup(v => v.ApplyDeviceCaps(It.IsAny<ClientCapabilities>(), null))
            .Returns((ClientCapabilities caps, DeviceCapabilities? _) => caps);
        storage.Setup(s => s.CombinePath(file.HostFolder, file.Filename)).Returns(info.FilePath);
        LiveTranscodeService service = new(
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
            Mock.Of<ILiveIngestKeyStore>(),
            NullLogger<LiveTranscodeService>.Instance
        );
        StartLiveSessionRequest request = new(
            file.Id.ToString(),
            new ClientCapabilitiesDto(null, null, ["mp4"], false, 10000),
            0,
            null
        );

        LiveResult started = await service.StartSessionAsync(
            owner,
            request,
            null,
            CancellationToken.None
        );
        started.Kind.Should().Be(LiveResultKind.Ok);
        StartLiveSessionResponse response = (StartLiveSessionResponse)started.Payload!;
        LiveTranscodeController controller = new(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(
                        new ClaimsIdentity(
                            [new Claim(ClaimTypes.NameIdentifier, owner.ToString())],
                            "TestAuth"
                        )
                    ),
                },
            },
        };
        ContentResult masterResponse = Assert.IsType<ContentResult>(
            controller.GetMasterPlaylist(response.SessionId)
        );
        (masterResponse.StatusCode ?? 200).Should().Be(200);
        string master = masterResponse.Content!;
        MatchCollection audioUris = Regex.Matches(
            master,
            "#EXT-X-MEDIA:TYPE=AUDIO[^\n]*URI=\"([^\"]+)\""
        );
        audioUris.Count.Should().Be(2);
        string videoUri = master
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Last(line => !line.StartsWith('#'))
            .Trim();
        List<string> renditionUris =
        [
            videoUri,
            .. audioUris.Select(match => match.Groups[1].Value),
        ];

        foreach (string uri in renditionUris)
        {
            Uri absolute = new(new Uri("https://example.test" + response.PlaylistUrl), uri);
            System.Text.RegularExpressions.Match sessionId = Regex.Match(
                absolute.AbsolutePath,
                @"/sessions/([^/]+)/playlist\.m3u8$"
            );
            sessionId.Success.Should().BeTrue($"master URI {uri} must point to a live playlist");
            ContentResult playlist = Assert.IsType<ContentResult>(
                controller.GetPlaylist(sessionId.Groups[1].Value)
            );
            (playlist.StatusCode ?? 200).Should().Be(200, $"owner must load {uri}");
            playlist.Content.Should().StartWith("#EXTM3U").And.Contain("#EXTINF:");
            service
                .GetPlaylist(stranger, sessionId.Groups[1].Value)
                .Kind.Should()
                .Be(LiveResultKind.NotFound);
        }

        sessions
            .ActiveSessionCount.Should()
            .Be(1, "audio children must not consume viewer session slots");
        sessions
            .GetUserSessionIds(owner.ToString())
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(response.SessionId);
    }
}
