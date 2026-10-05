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
using Newtonsoft.Json.Linq;
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.BuildingBlocks;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.Hdr;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Pipeline.Stages;
using EncodingProfile = NoMercy.Encoder.Profiles.EncodingProfile;

namespace NoMercy.Tests.Encoder.Pipeline.Stages;

/// <summary>
/// Regression net for the field report "legacy profiles encode in HEVC".
/// Feeds the EXACT v2 ProfileJson row stored for the migrated v1 preset
/// "1080p regular" (Codec=H264) through the real plan pipeline — real
/// CodecRegistry, CodecResolver, HardwarePreferenceResolver and
/// BitDepthPolicyResolver — on a GPU host whose SpeedIndex carries both
/// h264_nvenc and hevc_nvenc benchmarks (Fillz's GTX 1060 shape). The
/// resolved encoder must stay in the H264 family for every source type.
/// </summary>
public class LegacyProfileCodecRegressionTests
{
    private const string GpuName = "NVIDIA GeForce GTX 1060 3GB";

    [Theory]
    [InlineData("hdr-4k")]
    [InlineData("sdr-1080p")]
    public async Task LegacyH264Profile_OnGpuHost_NeverResolvesHevcEncoder(string sourceKind)
    {
        EncodingProfile profile = LoadStoredLegacyProfile();
        MediaInfo media = sourceKind == "hdr-4k" ? BuildHdr4KSource() : BuildSdr1080PSource();

        OutputPlan plan = await RunRealPlan(media, profile);

        VideoOutputPlan video = Assert.Single(plan.VideoOutputs);
        video
            .EncoderName.Should()
            .Match(
                encoderName => encoderName == "libx264" || encoderName.StartsWith("h264_"),
                "profile stores Codec=0 (H264); resolved encoder was {0}",
                video.EncoderName
            );
    }

    [Fact]
    public async Task LegacyH264Profile_NoHardware_ResolvesLibx264()
    {
        EncodingProfile profile = LoadStoredLegacyProfile();
        MediaInfo media = BuildSdr1080PSource();

        OutputPlan plan = await RunRealPlan(media, profile, withGpu: false);

        VideoOutputPlan video = Assert.Single(plan.VideoOutputs);
        video.EncoderName.Should().Be("libx264");
    }

    [Fact]
    public async Task LegacyH264Profile_StaleNvencBenchmark_FallsBackToLibx264()
    {
        // Field failure shape: the persisted SpeedIndex still carries nvenc
        // measurements from an older ffmpeg build, but the current bundled
        // ffmpeg has no hardware encoders. The resolver must not emit the
        // stale handle — that produces "Unknown encoder", zero variants, and
        // a header-only master playlist.
        EncodingProfile profile = LoadStoredLegacyProfile();
        MediaInfo media = BuildSdr1080PSource();

        OutputPlan plan = await RunRealPlan(media, profile, withGpu: true, nvencInFfmpeg: false);

        VideoOutputPlan video = Assert.Single(plan.VideoOutputs);
        video.EncoderName.Should().Be("libx264");
    }

    private static EncodingProfile LoadStoredLegacyProfile()
    {
        string fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Profiles",
            "V2",
            "Fixtures",
            "legacy-1080p-regular.json"
        );

        // Same parse path as PresetResolver.Resolve: JObject → ToObject.
        JObject stored = JObject.Parse(File.ReadAllText(fixturePath));
        EncodingProfile? profile = stored.ToObject<EncodingProfile>();
        profile.Should().NotBeNull();
        profile!.Video.Should().NotBeNull();
        ((int)profile.Video!.Codec).Should().Be(0);

        return profile;
    }

    private static async Task<OutputPlan> RunRealPlan(
        MediaInfo media,
        EncodingProfile profile,
        bool withGpu = true,
        bool nvencInFfmpeg = true
    )
    {
        CodecRegistry registry = new();
        CodecResolver codecResolver = new(registry);
        HardwarePreferenceResolver hardwarePreferenceResolver = new();
        BitDepthPolicyResolver bitDepthPolicyResolver = new();

        GpuDevice gpu = new(
            Vendor: GpuVendor.Nvidia,
            Name: GpuName,
            VramMb: 3072,
            MaxEncoderSessions: 3,
            SupportedCodecs: [VideoCodecType.H264, VideoCodecType.H265]
        );

        Mock<IHardwareCapabilities> hardware = new();
        hardware.Setup(h => h.HasGpu).Returns(withGpu);
        hardware.Setup(h => h.CpuCores).Returns(16);
        hardware.Setup(h => h.Gpus).Returns(withGpu ? [gpu] : []);
        hardware
            .Setup(h => h.SupportsHardwareEncoding(It.IsAny<VideoCodecType>()))
            .Returns(withGpu);
        hardware
            .Setup(h => h.GetGpuForCodec(It.IsAny<VideoCodecType>()))
            .Returns(withGpu ? gpu : null);
        // Real hardware-encoder init probe authority: nvenc is only
        // selectable when the GPU is present AND ffmpeg actually carries the
        // nvenc encoder — mirroring what HardwareEncoderProbe would confirm
        // on Fillz's GTX 1060 host.
        hardware
            .Setup(h => h.UsableHardwareEncoders)
            .Returns(
                withGpu && nvencInFfmpeg
                    ? new HashSet<string> { "h264_nvenc", "hevc_nvenc" }
                    : new HashSet<string>()
            );

        HashSet<string> encoders =
            withGpu && nvencInFfmpeg
                ? ["libx264", "libx265", "h264_nvenc", "hevc_nvenc", "aac", "libsvtav1"]
                : ["libx264", "libx265", "aac", "libsvtav1"];

        Mock<IFfmpegCapabilities> ffmpegCapabilities = new();
        ffmpegCapabilities.Setup(c => c.AvailableEncoders).Returns(encoders);
        ffmpegCapabilities.Setup(c => c.AvailableFilters).Returns(new HashSet<string>());
        ffmpegCapabilities
            .Setup(c => c.HasEncoder(It.IsAny<string>()))
            .Returns((string name) => encoders.Contains(name));
        ffmpegCapabilities.Setup(c => c.HasFilter(It.IsAny<string>())).Returns(false);

        DateTime measuredAt = DateTime.UtcNow;
        SpeedIndex speedIndex = new(
            new()
            {
                [new(VideoCodecType.H264, "h264_nvenc", 1920, GpuName)] = new(
                    240,
                    10.0,
                    measuredAt
                ),
                [new(VideoCodecType.H265, "hevc_nvenc", 1920, GpuName)] = new(190, 7.9, measuredAt),
                [new(VideoCodecType.H264, "libx264", 1920, null)] = new(62, 2.6, measuredAt),
                [new(VideoCodecType.H265, "libx265", 1920, null)] = new(18, 0.7, measuredAt),
            }
        );

        PlanStage stage = new(
            new(),
            new(),
            new(),
            codecResolver,
            hardware.Object,
            new TonemapSelector(),
            ffmpegCapabilities.Object,
            new AbrLadderGenerator(),
            new NoOpCropDetector(),
            NullLogger<PlanStage>.Instance,
            hardwarePreferenceResolver: hardwarePreferenceResolver,
            speedIndex: speedIndex,
            bitDepthPolicyResolver: bitDepthPolicyResolver
        );

        ValidateInput input = new(media, profile);
        EncodingContext context = EncodingContext.Create();
        StageResult result = await stage.ExecuteAsync(input, context, CancellationToken.None);

        StageSuccess<ExecutionPlan> success = Assert.IsType<StageSuccess<ExecutionPlan>>(result);
        return success.Value.OutputPlan;
    }

    private static MediaInfo BuildHdr4KSource() =>
        new(
            FilePath: "/movies/Iron.Man.(2008)/Iron.Man.mkv",
            Format: "matroska",
            Duration: TimeSpan.FromMinutes(126),
            OverallBitRateKbps: 52000,
            FileSizeBytes: 49_000_000_000,
            VideoStreams:
            [
                new(
                    Index: 0,
                    Codec: "hevc",
                    Width: 3840,
                    Height: 2160,
                    FrameRate: 23.976,
                    BitDepth: 10,
                    PixelFormat: "yuv420p10le",
                    ColorPrimaries: "bt2020",
                    ColorTransfer: "smpte2084",
                    ColorSpace: "bt2020nc",
                    IsDefault: true,
                    BitRateKbps: 48000
                ),
            ],
            AudioStreams:
            [
                new(
                    Index: 1,
                    Codec: "truehd",
                    Channels: 8,
                    SampleRate: 48000,
                    BitRateKbps: 4500,
                    Language: "eng",
                    IsDefault: true,
                    IsForced: false
                ),
            ],
            SubtitleStreams: [],
            Chapters: []
        );

    // BitDepth is 10 (not the legacy profile's requested 8) so this source
    // never qualifies for PlanStage's smart-copy downgrade (an exact
    // codec/resolution/bit-depth match would stream-copy instead of
    // transcoding) — these tests exist to prove the CODEC RESOLUTION path
    // (hardware selection, stale-benchmark fallback) never picks HEVC, which
    // only runs when the source actually needs a re-encode.
    private static MediaInfo BuildSdr1080PSource() =>
        new(
            FilePath: "/movies/test/test.mkv",
            Format: "matroska",
            Duration: TimeSpan.FromMinutes(110),
            OverallBitRateKbps: 12000,
            FileSizeBytes: 9_000_000_000,
            VideoStreams:
            [
                new(
                    Index: 0,
                    Codec: "h264",
                    Width: 1920,
                    Height: 1080,
                    FrameRate: 24.0,
                    BitDepth: 10,
                    PixelFormat: "yuv420p10le",
                    ColorPrimaries: "bt709",
                    ColorTransfer: "bt709",
                    ColorSpace: "bt709",
                    IsDefault: true,
                    BitRateKbps: 10000
                ),
            ],
            AudioStreams:
            [
                new(
                    Index: 1,
                    Codec: "ac3",
                    Channels: 6,
                    SampleRate: 48000,
                    BitRateKbps: 640,
                    Language: "eng",
                    IsDefault: true,
                    IsForced: false
                ),
            ],
            SubtitleStreams: [],
            Chapters: []
        );
}
