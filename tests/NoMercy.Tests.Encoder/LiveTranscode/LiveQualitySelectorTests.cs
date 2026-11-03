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

using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.LiveTranscode;

namespace NoMercy.Tests.Encoder.LiveTranscode;

public class LiveQualitySelectorTests
{
    private static IHardwareCapabilities MakeGpuHardware() =>
        new HardwareCapabilities(
            Gpus:
            [
                new(
                    Vendor: GpuVendor.Nvidia,
                    Name: "RTX 4090",
                    VramMb: 24576,
                    MaxEncoderSessions: 12,
                    SupportedCodecs: [VideoCodecType.H264, VideoCodecType.H265, VideoCodecType.Av1]
                ),
            ],
            CpuCores: 16
        );

    private static IHardwareCapabilities MakeSoftwareHardware() =>
        new HardwareCapabilities(Gpus: [], CpuCores: 8);

    private static IResourceBudget MakeBudget(IHardwareCapabilities hardware) =>
        new ResourceBudget(hardware.Gpus, hardware.CpuCores);

    private readonly LiveQualitySelector _gpuSelector = new(
        new CodecResolver(new()),
        MakeGpuHardware()
    );

    private readonly LiveQualitySelector _softwareSelector = new(
        new CodecResolver(new()),
        MakeSoftwareHardware()
    );

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static MediaInfo MakeMedia(int width, int height) =>
        new(
            FilePath: "/media/test.mkv",
            Format: "matroska,webm",
            Duration: TimeSpan.FromMinutes(90),
            OverallBitRateKbps: 15000,
            FileSizeBytes: 10_000_000_000L,
            VideoStreams:
            [
                new(
                    Index: 0,
                    Codec: "h264",
                    Width: width,
                    Height: height,
                    FrameRate: 24.0,
                    BitDepth: 8,
                    PixelFormat: "yuv420p",
                    ColorPrimaries: "bt709",
                    ColorTransfer: "bt709",
                    ColorSpace: "bt709",
                    IsDefault: true,
                    BitRateKbps: 15000
                ),
            ],
            AudioStreams: [],
            SubtitleStreams: [],
            Chapters: []
        );

    private static ClientCapabilities MakeClient(int maxWidth = 7680, int maxHeight = 4320) =>
        new(
            SupportedVideoCodecs: [VideoCodecType.H264, VideoCodecType.H265],
            SupportedAudioCodecs: [AudioCodecType.Aac],
            SupportedContainers: ["mp4", "mkv"],
            MaxWidth: maxWidth,
            MaxHeight: maxHeight,
            SupportsHdr: false,
            Supports10Bit: false,
            MaxBitrateKbps: 0
        );

    private static SpeedIndex MakeFastGpuSpeedIndex() =>
        new(
            new()
            {
                // Client lists H264 first (its own preference order), so the selector
                // targets H264 → resolves h264_nvenc on NVIDIA.
                [new(VideoCodecType.H264, "h264_nvenc", 3840, "RTX 4090")] = new(
                    100.0,
                    4.0,
                    DateTime.UtcNow
                ),
                [new(VideoCodecType.H264, "h264_nvenc", 1920, "RTX 4090")] = new(
                    180.0,
                    7.5,
                    DateTime.UtcNow
                ),
                [new(VideoCodecType.H264, "h264_nvenc", 1280, "RTX 4090")] = new(
                    240.0,
                    10.0,
                    DateTime.UtcNow
                ),
                [new(VideoCodecType.H264, "h264_nvenc", 854, "RTX 4090")] = new(
                    300.0,
                    12.5,
                    DateTime.UtcNow
                ),
            }
        );

    private static SpeedIndex MakeEmptySpeedIndex() => new(new());

    // ──────────────────────────────────────────────────────────────────────────
    // Target codec honours client preference order
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ClientListsH264First_TargetsH264NotH265()
    {
        // Regression guard: the selector used to force an H265-first server order,
        // handing HEVC to a browser that listed H264 first for reliability. It must
        // honour the client's own order — here H264.
        IHardwareCapabilities hardware = MakeGpuHardware();
        MediaInfo media = MakeMedia(1920, 1080);
        ClientCapabilities client = MakeClient(); // lists [H264, H265]
        SpeedIndex speeds = MakeFastGpuSpeedIndex();
        IResourceBudget budget = MakeBudget(hardware);

        LiveQuality[] qualities = _gpuSelector.GetAvailableQualities(media, client, speeds, budget);

        qualities.Should().NotBeEmpty();
        qualities.Should().OnlyContain(q => q.Codec == VideoCodecType.H264);
    }

    [Fact]
    public void NewShapeOnlyClient_TargetsCodecFromVideoArray_NotAnyCodec()
    {
        // A client sending only the new per-codec Video array (no legacy
        // SupportedVideoCodecs) must still be filtered by its declared codec —
        // it must not silently fall through to "any codec allowed".
        IHardwareCapabilities hardware = MakeGpuHardware();
        MediaInfo media = MakeMedia(1920, 1080);
        ClientCapabilities client = new(
            Video:
            [
                new(
                    Codec: VideoCodecType.H265,
                    Profiles: [],
                    MaxBitDepth: 8,
                    MaxWidth: 7680,
                    MaxHeight: 4320,
                    MaxFramerate: 60,
                    HdrFormats: [],
                    MaxBitrateKbps: 0
                ),
            ],
            MaxWidth: 7680,
            MaxHeight: 4320
        );
        SpeedIndex speeds = MakeFastGpuSpeedIndex();
        IResourceBudget budget = MakeBudget(hardware);

        LiveQuality[] qualities = _gpuSelector.GetAvailableQualities(media, client, speeds, budget);
        LiveQuality optimal = _gpuSelector.SelectOptimal(media, client, speeds, budget);

        qualities.Should().OnlyContain(q => q.Codec == VideoCodecType.H265);
        optimal.Codec.Should().Be(VideoCodecType.H265);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // GetAvailableQualities
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FourK_Input_FastGpu_ProducesQualities_IncludingFourK()
    {
        IHardwareCapabilities hardware = MakeGpuHardware();
        MediaInfo media = MakeMedia(3840, 2160);
        ClientCapabilities client = MakeClient();
        SpeedIndex speeds = MakeFastGpuSpeedIndex();
        IResourceBudget budget = MakeBudget(hardware);

        LiveQuality[] qualities = _gpuSelector.GetAvailableQualities(media, client, speeds, budget);

        qualities.Should().NotBeEmpty();
        qualities.Should().Contain(q => q.Width == 3840 && q.Height == 2160);
    }

    [Fact]
    public void FourK_Input_SkipsResolutionsLargerThanSource()
    {
        IHardwareCapabilities hardware = MakeGpuHardware();
        MediaInfo media = MakeMedia(1280, 720);
        ClientCapabilities client = MakeClient();
        SpeedIndex speeds = MakeFastGpuSpeedIndex();
        IResourceBudget budget = MakeBudget(hardware);

        LiveQuality[] qualities = _gpuSelector.GetAvailableQualities(media, client, speeds, budget);

        qualities.Should().NotContain(q => q.Width > 1280);
    }

    [Fact]
    public void NoSpeedData_AllMarkedCanRealtimeFalse()
    {
        IHardwareCapabilities hardware = MakeGpuHardware();
        MediaInfo media = MakeMedia(1920, 1080);
        ClientCapabilities client = MakeClient();
        SpeedIndex speeds = MakeEmptySpeedIndex();
        IResourceBudget budget = MakeBudget(hardware);

        LiveQuality[] qualities = _gpuSelector.GetAvailableQualities(media, client, speeds, budget);

        qualities.Should().NotBeEmpty();
        qualities.Should().OnlyContain(q => q.CanRealtime == false);
    }

    [Fact]
    public void FastGpu_HighSpeedMultiplier_MarksCanRealtimeTrue()
    {
        IHardwareCapabilities hardware = MakeGpuHardware();
        MediaInfo media = MakeMedia(1920, 1080);
        ClientCapabilities client = MakeClient();
        SpeedIndex speeds = MakeFastGpuSpeedIndex();
        IResourceBudget budget = MakeBudget(hardware);

        LiveQuality[] qualities = _gpuSelector.GetAvailableQualities(media, client, speeds, budget);

        qualities.Should().Contain(q => q.CanRealtime);
    }

    [Fact]
    public void SoftwareOnly_IsHardwareAcceleratedFalse()
    {
        IHardwareCapabilities hardware = MakeSoftwareHardware();
        MediaInfo media = MakeMedia(1920, 1080);
        ClientCapabilities client = MakeClient();
        SpeedIndex speeds = MakeEmptySpeedIndex();
        IResourceBudget budget = MakeBudget(hardware);

        LiveQuality[] qualities = _softwareSelector.GetAvailableQualities(
            media,
            client,
            speeds,
            budget
        );

        qualities.Should().NotBeEmpty();
        qualities.Should().OnlyContain(q => q.IsHardwareAccelerated == false);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SelectOptimal
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FourK_FastGpu_SelectsHighestCanRealtime()
    {
        IHardwareCapabilities hardware = MakeGpuHardware();
        MediaInfo media = MakeMedia(3840, 2160);
        ClientCapabilities client = MakeClient();
        SpeedIndex speeds = MakeFastGpuSpeedIndex();
        IResourceBudget budget = MakeBudget(hardware);

        LiveQuality optimal = _gpuSelector.SelectOptimal(media, client, speeds, budget);

        optimal.CanRealtime.Should().BeTrue();
        optimal.Width.Should().Be(3840);
    }

    [Fact]
    public void NoSpeedData_FallsBackToLowestQuality()
    {
        IHardwareCapabilities hardware = MakeGpuHardware();
        MediaInfo media = MakeMedia(1920, 1080);
        ClientCapabilities client = MakeClient();
        SpeedIndex speeds = MakeEmptySpeedIndex();
        IResourceBudget budget = MakeBudget(hardware);

        LiveQuality optimal = _gpuSelector.SelectOptimal(media, client, speeds, budget);

        // No CanRealtime candidates → falls back to lowest resolution tier
        optimal.Should().NotBeNull();
        optimal.Width.Should().BeLessThanOrEqualTo(1920);
    }

    [Fact]
    public void Client_Max720p_CapsOutputAt720p()
    {
        IHardwareCapabilities hardware = MakeGpuHardware();
        MediaInfo media = MakeMedia(1920, 1080);
        ClientCapabilities client = MakeClient(maxWidth: 1280, maxHeight: 720);
        SpeedIndex speeds = MakeFastGpuSpeedIndex();
        IResourceBudget budget = MakeBudget(hardware);

        LiveQuality optimal = _gpuSelector.SelectOptimal(media, client, speeds, budget);

        optimal.Width.Should().BeLessThanOrEqualTo(1280);
        optimal.Height.Should().BeLessThanOrEqualTo(720);
    }

    [Fact]
    public void VideoOnlyClient_UsesTargetCodecResolutionLimit()
    {
        IHardwareCapabilities hardware = MakeGpuHardware();
        ClientCapabilities client = new(
            Video:
            [
                new(
                    Codec: VideoCodecType.H264,
                    Profiles: [],
                    MaxBitDepth: 8,
                    MaxWidth: 1920,
                    MaxHeight: 1080,
                    MaxFramerate: 60,
                    HdrFormats: [],
                    MaxBitrateKbps: 0
                ),
            ]
        );

        LiveQuality optimal = _gpuSelector.SelectOptimal(
            MakeMedia(3840, 2160),
            client,
            MakeFastGpuSpeedIndex(),
            MakeBudget(hardware)
        );

        optimal.Width.Should().Be(1920);
        optimal.Height.Should().Be(1080);
    }

    [Fact]
    public void SoftwareOnly_MarkedNotHardwareAccelerated()
    {
        IHardwareCapabilities hardware = MakeSoftwareHardware();
        MediaInfo media = MakeMedia(1280, 720);
        ClientCapabilities client = MakeClient();
        SpeedIndex speeds = MakeEmptySpeedIndex();
        IResourceBudget budget = MakeBudget(hardware);

        LiveQuality optimal = _softwareSelector.SelectOptimal(media, client, speeds, budget);

        optimal.IsHardwareAccelerated.Should().BeFalse();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Sub-smallest-tier sources (regression: empty candidate set must never
    // reach First()/throw — the smallest tier is always kept as a fallback)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SubSmallestTierSource_GetAvailableQualities_KeepsSmallestTier()
    {
        IHardwareCapabilities hardware = MakeGpuHardware();
        MediaInfo media = MakeMedia(320, 240);
        ClientCapabilities client = MakeClient();
        SpeedIndex speeds = MakeFastGpuSpeedIndex();
        IResourceBudget budget = MakeBudget(hardware);

        LiveQuality[] qualities = _gpuSelector.GetAvailableQualities(media, client, speeds, budget);

        qualities.Should().ContainSingle();
        qualities[0].Width.Should().Be(854);
        qualities[0].Height.Should().Be(480);
    }

    [Fact]
    public void SubSmallestTierSource_SelectOptimal_SelectsSmallestTierWithoutThrowing()
    {
        IHardwareCapabilities hardware = MakeGpuHardware();
        MediaInfo media = MakeMedia(320, 240);
        ClientCapabilities client = MakeClient();
        SpeedIndex speeds = MakeFastGpuSpeedIndex();
        IResourceBudget budget = MakeBudget(hardware);

        Action act = () => _gpuSelector.SelectOptimal(media, client, speeds, budget);

        act.Should().NotThrow();

        LiveQuality optimal = _gpuSelector.SelectOptimal(media, client, speeds, budget);
        optimal.Width.Should().Be(854);
        optimal.Height.Should().Be(480);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SelectForBandwidth — fits a tier directly to the client's observed
    // downlink at usableFraction, independent of the encoder-lead signal.
    // ──────────────────────────────────────────────────────────────────────────

    private static LiveQuality MakeTier(string id, int bitrateKbps) =>
        new(
            Id: id,
            Label: id,
            Width: 1920,
            Height: 1080,
            Codec: VideoCodecType.H264,
            BitrateKbps: bitrateKbps,
            Encoder: "libx264",
            IsHardwareAccelerated: false,
            ExpectedSpeed: 2.0,
            CanRealtime: true
        );

    private static readonly LiveQuality Tier1080 = MakeTier("1080p", 8000);
    private static readonly LiveQuality Tier720 = MakeTier("720p", 4000);
    private static readonly LiveQuality Tier480 = MakeTier("480p", 2000);
    private static readonly LiveQuality[] Tiers = [Tier1080, Tier720, Tier480];

    [Fact]
    public void SelectForBandwidth_AmpleBandwidth_PicksHighestTier()
    {
        // 10000 kbps * 0.8 = 8000 kbps usable — exactly fits the 1080p tier.
        LiveQuality selected = _gpuSelector.SelectForBandwidth(Tiers, 10000, 0.8, Tier480);

        selected.Id.Should().Be("1080p");
    }

    [Fact]
    public void SelectForBandwidth_MidBandwidth_PicksMiddleTier()
    {
        // 5500 kbps * 0.8 = 4400 kbps usable — too little for 1080p (8000),
        // fits 720p (4000).
        LiveQuality selected = _gpuSelector.SelectForBandwidth(Tiers, 5500, 0.8, Tier1080);

        selected.Id.Should().Be("720p");
    }

    [Fact]
    public void SelectForBandwidth_ExactBoundary_FitsTheBoundaryTier()
    {
        // 4000 kbps * 1.0 = 4000 kbps usable — exactly equals the 720p tier's
        // bitrate, so the boundary itself must fit (<=), not be excluded.
        LiveQuality selected = _gpuSelector.SelectForBandwidth(Tiers, 4000, 1.0, Tier480);

        selected.Id.Should().Be("720p");
    }

    [Fact]
    public void SelectForBandwidth_StarvedBandwidth_FallsBackToLowestTier()
    {
        // 500 kbps * 0.8 = 400 kbps usable — fits nothing; must fall back to
        // the lowest tier rather than return empty.
        LiveQuality selected = _gpuSelector.SelectForBandwidth(Tiers, 500, 0.8, Tier1080);

        selected.Id.Should().Be("480p");
    }

    [Fact]
    public void SelectForBandwidth_EmptyAvailable_FallsBackToCurrent()
    {
        LiveQuality selected = _gpuSelector.SelectForBandwidth([], 10000, 0.8, Tier720);

        selected.Id.Should().Be("720p");
    }
}
