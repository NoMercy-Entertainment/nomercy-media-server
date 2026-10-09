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
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.BuildingBlocks;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.ContentAnalysis;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.Hdr;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Pipeline.Stages;
using CodecProfile = NoMercy.Encoder.Profiles.CodecProfile;
using Container = NoMercy.Encoder.Profiles.Container;
using EncodingProfile = NoMercy.Encoder.Profiles.EncodingProfile;
using StreamPolicy = NoMercy.Encoder.Profiles.StreamPolicy;
using V2RateControlMode = NoMercy.Encoder.Profiles.RateControlMode;

namespace NoMercy.Tests.Encoder.Pipeline.Stages;

/// <summary>
/// Matching the target on codec, resolution and bit depth only says the source
/// is the right shape. A 1080p HEVC 10-bit source can still carry several times
/// the bitrate the profile would spend on the same picture, and copying it then
/// throws away the entire point of the re-encode. These pin the rule: copy when
/// touching the file cannot make it smaller, re-encode when a measurement says
/// it can.
/// </summary>
[Trait("Category", "Unit")]
public class PlanStageEncodeYieldTests
{
    private const long SourceKbps = 12000;

    private readonly Mock<ICodecResolver> _codecResolver = new();
    private readonly Mock<IHardwareCapabilities> _hardware = new();
    private readonly EncodingContext _context = EncodingContext.Create();

    public PlanStageEncodeYieldTests()
    {
        _hardware.Setup(h => h.HasGpu).Returns(false);
        _hardware.Setup(h => h.CpuCores).Returns(8);
        _hardware.Setup(h => h.Gpus).Returns([]);
        _hardware.Setup(h => h.SupportsHardwareEncoding(It.IsAny<VideoCodecType>())).Returns(false);
        _hardware
            .Setup(h => h.GetGpuForCodec(It.IsAny<VideoCodecType>()))
            .Returns((GpuDevice?)null);

        _codecResolver
            .Setup(r =>
                r.Resolve(
                    VideoCodecType.Copy,
                    It.IsAny<IHardwareCapabilities>(),
                    It.IsAny<EncoderPreference>()
                )
            )
            .Returns(BuildResolvedCodec("copy"));

        _codecResolver
            .Setup(r =>
                r.Resolve(
                    VideoCodecType.H265,
                    It.IsAny<IHardwareCapabilities>(),
                    It.IsAny<EncoderPreference>()
                )
            )
            .Returns(BuildResolvedCodec("libx265"));
    }

    private PlanStage BuildStage(IEncodeYieldProbe? probe) =>
        new(
            new(),
            new(),
            new(),
            _codecResolver.Object,
            _hardware.Object,
            new TonemapSelector(),
            new Mock<IFfmpegCapabilities>().Object,
            new AbrLadderGenerator(),
            new NoOpCropDetector(),
            NullLogger<PlanStage>.Instance,
            encodeYieldProbe: probe
        );

    private static IEncodeYieldProbe ProbeReturning(long? kbps)
    {
        Mock<IEncodeYieldProbe> probe = new();
        probe
            .Setup(p =>
                p.EstimateBitrateKbpsAsync(
                    It.IsAny<string>(),
                    It.IsAny<EncodeYieldTarget>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(kbps);
        return probe.Object;
    }

    private static ResolvedCodec BuildResolvedCodec(string ffmpegName) =>
        new(
            FfmpegEncoderName: ffmpegName,
            EncoderInfo: new(
                FfmpegName: ffmpegName,
                RequiredVendor: null,
                Presets: ffmpegName == "copy" ? [] : ["medium"],
                Profiles: ffmpegName == "copy" ? [] : ["main", "main10"],
                Levels: ffmpegName == "copy" ? [] : ["5.1"],
                QualityRange: new(0, 51, 23),
                SupportedRateControl: [RateControlMode.Crf],
                Supports10Bit: true,
                SupportsHdr: true,
                MaxConcurrentSessions: int.MaxValue,
                PixelFormat10Bit: "yuv420p10le",
                VendorSpecificFlags: new()
            ),
            Device: null,
            DefaultRateControl: RateControlMode.Crf
        );

    private static MediaInfo BuildMedia() =>
        new(
            FilePath: "/anime/episode.mkv",
            Format: "matroska",
            Duration: TimeSpan.FromMinutes(24),
            OverallBitRateKbps: SourceKbps + 400,
            FileSizeBytes: 2_200_000_000,
            VideoStreams:
            [
                new(
                    Index: 0,
                    Codec: "hevc",
                    Width: 1920,
                    Height: 1080,
                    FrameRate: 23.976,
                    BitDepth: 10,
                    PixelFormat: "yuv420p10le",
                    ColorPrimaries: "bt709",
                    ColorTransfer: "bt709",
                    ColorSpace: "bt709",
                    IsDefault: true,
                    BitRateKbps: SourceKbps
                ),
            ],
            AudioStreams: [],
            SubtitleStreams: [],
            Chapters: []
        );

    private static EncodingProfile BuildProfile() =>
        new(
            Id: Ulid.NewUlid(),
            Name: "Anime 1080p HEVC 10-bit",
            Container: Container.HlsFmp4,
            Video: new(
                Policy: StreamPolicy.Transcode,
                Codec: VideoCodecType.H265,
                Width: 1920,
                Height: 1080,
                RateControl: V2RateControlMode.Crf,
                Crf: 20,
                BitrateKbps: 0,
                MaxBitrateKbps: null,
                BufferSizeKbps: null,
                Preset: "medium",
                CodecProfile: CodecProfile.Auto,
                Level: null,
                Tune: "animation",
                BitDepth: 10,
                PixelFormat: null,
                KeyframeIntervalSeconds: 2,
                ConvertHdrToSdr: false,
                SegmentNameTemplate: "video/{label}",
                PlaylistNameTemplate: "video/{label}/playlist"
            ),
            Audio: [],
            Subtitles: [],
            HlsDerivatives: new NoMercy.Encoder.Profiles.HlsDerivatives
            {
                GenerateSpriteVtt = false,
            }
        );

    private async Task<string> EncoderNameFor(IEncodeYieldProbe? probe)
    {
        StageResult result = await BuildStage(probe)
            .ExecuteAsync(new(BuildMedia(), BuildProfile()), _context, default);

        StageSuccess<ExecutionPlan> success = Assert.IsType<StageSuccess<ExecutionPlan>>(result);
        return Assert.Single(success.Value.OutputPlan.VideoOutputs).EncoderName;
    }

    [Fact]
    public async Task SourceCarryingFarMoreBitrateThanTheTargetSpends_IsReencoded()
    {
        // 2000 against a 12000 kbps source — the shape matches, the size does not.
        string encoder = await EncoderNameFor(ProbeReturning(2000));

        encoder
            .Should()
            .Be("libx265", "a measured six-fold saving is the reason to re-encode, not to skip it");
    }

    [Fact]
    public async Task SourceAlreadyNearWhatTheTargetWouldSpend_IsCopied()
    {
        // 10000 against 12000 — re-encoding buys 17% and costs generation loss.
        string encoder = await EncoderNameFor(ProbeReturning(10000));

        encoder.Should().Be("copy", "a marginal gain does not justify decoding every frame");
    }

    [Fact]
    public async Task ProbeThatCannotMeasure_FallsBackToCopying()
    {
        string encoder = await EncoderNameFor(ProbeReturning(null));

        encoder.Should().Be("copy", "an unknown yield must not be read as a reason to re-encode");
    }

    [Fact]
    public async Task NoProbeConfigured_KeepsTheOriginalCopyBehaviour()
    {
        string encoder = await EncoderNameFor(null);

        encoder.Should().Be("copy");
    }
}
