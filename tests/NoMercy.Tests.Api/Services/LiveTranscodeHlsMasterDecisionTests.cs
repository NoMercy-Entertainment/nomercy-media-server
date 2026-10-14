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

using System.Linq.Expressions;
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

[Trait("Category", "Unit")]
public class LiveTranscodeHlsMasterDecisionTests
{
    private const string MasterPath = "show/episode/master.m3u8";

    private const string Avc1SdrMaster =
        "#EXTM3U\n"
        + "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio_aac\",NAME=\"eng\",LANGUAGE=\"eng\",DEFAULT=YES,CHANNELS=\"2\",URI=\"audio_eng_aac/audio_eng_aac.m3u8\"\n"
        + "#EXT-X-STREAM-INF:BANDWIDTH=14168076,AVERAGE-BANDWIDTH=8799901,RESOLUTION=1918x802,FRAME-RATE=23.976,CODECS=\"avc1.640028,mp4a.40.2\",VIDEO-RANGE=SDR,AUDIO=\"audio_aac\"\n"
        + "video_1918x802_SDR/video_1918x802_SDR.m3u8\n"
        + "#EXT-X-STREAM-INF:BANDWIDTH=4000000,AVERAGE-BANDWIDTH=3000000,RESOLUTION=1280x534,FRAME-RATE=23.976,CODECS=\"avc1.64001f,mp4a.40.2\",VIDEO-RANGE=SDR,AUDIO=\"audio_aac\"\n"
        + "video_1280x534_SDR/video_1280x534_SDR.m3u8\n";

    private const string HevcHdrMaster =
        "#EXTM3U\n"
        + "#EXT-X-STREAM-INF:BANDWIDTH=14168076,AVERAGE-BANDWIDTH=8799901,RESOLUTION=3840x2160,FRAME-RATE=23.976,CODECS=\"hev1.2.4.L150.B0,mp4a.40.2\",VIDEO-RANGE=PQ,AUDIO=\"audio_aac\"\n"
        + "video_3840x2160_HDR/video_3840x2160_HDR.m3u8\n";

    private const string MissingCodecsMaster =
        "#EXTM3U\n"
        + "#EXT-X-STREAM-INF:BANDWIDTH=14168076,AVERAGE-BANDWIDTH=8799901,RESOLUTION=1918x802,FRAME-RATE=23.976,CODECS=\"avc1.640028,mp4a.40.2\",VIDEO-RANGE=SDR\n"
        + "video_1918x802_SDR/video_1918x802_SDR.m3u8\n"
        + "#EXT-X-STREAM-INF:BANDWIDTH=4000000,AVERAGE-BANDWIDTH=3000000,RESOLUTION=1280x534,FRAME-RATE=23.976,VIDEO-RANGE=SDR\n"
        + "video_1280x534_SDR/video_1280x534_SDR.m3u8\n";

    [Fact]
    public async Task Avc1SdrMaster_WithH264Client_DirectPlaysWithoutProbing()
    {
        Harness harness = new(Avc1SdrMaster);

        LiveResult result = await harness.StartAsync(H264OnlyClient());

        AssertDirect(result);
        harness.Analyzer.Verify(AnalyzeCall, Times.Never);
    }

    [Fact]
    public async Task HevcHdrMaster_WithHdrCapableClient_DirectPlaysWithoutProbing()
    {
        Harness harness = new(HevcHdrMaster);

        LiveResult result = await harness.StartAsync(HevcHdrClient());

        AssertDirect(result);
        harness.Analyzer.Verify(AnalyzeCall, Times.Never);
    }

    [Fact]
    public async Task HevcHdrMaster_WithH264OnlyClient_StillProbes()
    {
        Harness harness = new(HevcHdrMaster);

        await harness.StartAsync(H264OnlyClient());

        harness.Analyzer.Verify(AnalyzeCall, Times.Once);
    }

    [Fact]
    public async Task Master_WithAVariantMissingCodecs_StillProbes()
    {
        Harness harness = new(MissingCodecsMaster);

        await harness.StartAsync(H264OnlyClient());

        harness.Analyzer.Verify(AnalyzeCall, Times.Once);
    }

    [Fact]
    public async Task UnreadableMaster_StillProbes()
    {
        Harness harness = new(null);

        await harness.StartAsync(H264OnlyClient());

        harness.Analyzer.Verify(AnalyzeCall, Times.Once);
    }

    private static void AssertDirect(LiveResult result)
    {
        result.Kind.Should().Be(LiveResultKind.Ok);
        StartLiveSessionResponse response = (StartLiveSessionResponse)result.Payload!;
        response.Mode.Should().Be("direct");
    }

    private static Expression<Func<IMediaAnalyzer, Task<MediaInfo>>> AnalyzeCall =>
        a =>
            a.AnalyzeAsync(
                It.IsAny<string>(),
                It.IsAny<string[]?>(),
                It.IsAny<CancellationToken>()
            );

    private static ClientCapabilitiesDto H264OnlyClient() =>
        new(
            [new VideoCodecCapabilityDto(VideoCodecType.H264, [], 8, 4096, 2160, 60, [], 0)],
            [new AudioCodecCapabilityDto(AudioCodecType.Aac, 2, false, true)],
            ["hls"],
            false,
            0
        );

    private static ClientCapabilitiesDto HevcHdrClient() =>
        new(
            [
                new VideoCodecCapabilityDto(
                    VideoCodecType.H265,
                    [],
                    10,
                    4096,
                    2160,
                    60,
                    ["hdr10"],
                    0
                ),
            ],
            [new AudioCodecCapabilityDto(AudioCodecType.Aac, 2, false, true)],
            ["hls"],
            true,
            0
        );

    private sealed class Harness
    {
        private readonly LiveTranscodeService _service;
        private readonly Guid _owner = Guid.NewGuid();
        private readonly VideoFile _file = new()
        {
            Filename = "master.m3u8",
            HostFolder = "show/episode",
        };

        public Mock<IMediaAnalyzer> Analyzer { get; } = new();

        public Harness(string? masterText)
        {
            Mock<IStorage> storage = new();
            storage.Setup(s => s.CombinePath(_file.HostFolder, _file.Filename)).Returns(MasterPath);
            storage
                .Setup(s => s.ReadAllTextAsync(MasterPath, It.IsAny<CancellationToken>()))
                .Returns(
                    masterText is null
                        ? Task.FromException<string>(new IOException("unreadable"))
                        : Task.FromResult(masterText)
                );

            Analyzer
                .Setup(AnalyzeCall)
                .ThrowsAsync(new InvalidOperationException("probe should not decide this"));

            Mock<IVideoFileRepository> files = new();
            files
                .Setup(r =>
                    r.GetForUserWithMetadataAsync(_file.Id, _owner, It.IsAny<CancellationToken>())
                )
                .ReturnsAsync(_file);

            Mock<IDeviceAwareVariantSelector> variants = new();
            variants
                .Setup(v => v.ApplyDeviceCaps(It.IsAny<ClientCapabilities>(), null))
                .Returns((ClientCapabilities caps, DeviceCapabilities? _) => caps);

            SessionManager sessions = new(new LiveSessionLimits());
            Mock<ILiveSegmentInventory> inventory = new();
            LiveStreamingService streaming = new(
                NullLogger<LiveStreamingService>.Instance,
                storage.Object,
                inventory.Object,
                sessionManager: sessions
            );

            _service = new(
                Mock.Of<ILiveEncoder>(),
                streaming,
                new LivePlaylistBuilder(),
                sessions,
                Analyzer.Object,
                Mock.Of<ILiveQualitySelector>(),
                new PlaybackDecisionEngine(),
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
        }

        public Task<LiveResult> StartAsync(ClientCapabilitiesDto caps) =>
            _service.StartSessionAsync(
                _owner,
                new StartLiveSessionRequest(_file.Id.ToString(), caps, 0, null),
                null,
                CancellationToken.None
            );
    }
}
