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

using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Hardware;
using CodecProfile = NoMercy.Encoder.Profiles.CodecProfile;
using StreamPolicy = NoMercy.Encoder.Profiles.StreamPolicy;
using V2RateControlMode = NoMercy.Encoder.Profiles.RateControlMode;
using VideoOutput = NoMercy.Encoder.Profiles.VideoOutput;

namespace NoMercy.Tests.Encoder.Codecs;

/// <summary>
/// Every H264 encoder family uses different flags for the same profile-level
/// CRF value. Getting the mapping wrong means silently-wrong bitrate on one
/// vendor while the others look fine — the kind of regression that only shows
/// up after a user re-encodes their whole library on new hardware. These
/// tests pin the mapping down per family.
/// </summary>
public class EncoderArgumentResolverTests
{
    private static readonly CodecRegistry Registry = new();

    // ──────────────────────────────────────────────────────────────────────────
    // ResolveQuality — CRF → vendor-specific flag
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ResolveQuality_Software_EmitsCrfDirectly()
    {
        ResolvedCodec resolved = ResolveH264("libx264", null, RateControlMode.Crf);
        Dictionary<string, string> flags = [];

        int crf = EncoderArgumentResolver.ResolveQuality(22, resolved, flags);

        crf.Should().Be(22, "libx264 accepts -crf directly");
        flags.Should().NotContainKey("-cq");
        flags.Should().NotContainKey("-qp");
        flags.Should().NotContainKey("-global_quality");
    }

    [Fact]
    public void ResolveQuality_Nvenc_MapsToVbrCq()
    {
        ResolvedCodec resolved = ResolveH264("h264_nvenc", GpuVendor.Nvidia, RateControlMode.Cq);
        Dictionary<string, string> flags = [];

        int crf = EncoderArgumentResolver.ResolveQuality(22, resolved, flags);

        crf.Should().Be(0, "NVENC doesn't accept -crf");
        flags["-rc"].Should().Be("vbr");
        flags["-cq"].Should().Be("22");
    }

    [Fact]
    public void ResolveQuality_Qsv_MapsToGlobalQuality()
    {
        ResolvedCodec resolved = ResolveH264("h264_qsv", GpuVendor.Intel, RateControlMode.Icq);
        Dictionary<string, string> flags = [];

        int crf = EncoderArgumentResolver.ResolveQuality(22, resolved, flags);

        crf.Should().Be(0);
        flags["-global_quality"].Should().Be("22", "Intel QSV uses ICQ via -global_quality");
        flags.Should().NotContainKey("-rc");
    }

    [Fact]
    public void ResolveQuality_Amf_MapsToCqpQp()
    {
        ResolvedCodec resolved = ResolveH264("h264_amf", GpuVendor.Amd, RateControlMode.Cqp);
        Dictionary<string, string> flags = [];

        int crf = EncoderArgumentResolver.ResolveQuality(22, resolved, flags);

        crf.Should().Be(0);
        flags["-rc"].Should().Be("cqp");
        flags["-qp"].Should().Be("22");
    }

    [Fact]
    public void ResolveQuality_Vaapi_MapsToCqpQp()
    {
        ResolvedCodec resolved = ResolveH264("h264_vaapi", GpuVendor.Intel, RateControlMode.Cqp);
        Dictionary<string, string> flags = [];

        int crf = EncoderArgumentResolver.ResolveQuality(22, resolved, flags);

        crf.Should().Be(0);
        flags["-rc"].Should().Be("cqp");
        flags["-qp"].Should().Be("22");
    }

    [Fact]
    public void ResolveQuality_VideoToolbox_MapsToQ_ScaledIntoPercentRange()
    {
        // VideoToolbox quality is 0-100 (higher = better). The profile's CRF
        // is H264 reference 0-51. Scaled: round(50/51*100)=98.
        // Without scaling, passing 50 raw would be mid-range quality instead
        // of near-lossless as the profile intended.
        ResolvedCodec resolved = ResolveH264(
            "h264_videotoolbox",
            GpuVendor.Apple,
            RateControlMode.QualityLevel
        );
        Dictionary<string, string> flags = [];

        int crf = EncoderArgumentResolver.ResolveQuality(50, resolved, flags);

        crf.Should().Be(0);
        flags["-q:v"]
            .Should()
            .Be("98", "50/51 of the 0-100 VideoToolbox range = 98 (near-max quality)");
        flags.Should().NotContainKey("-rc");
    }

    [Fact]
    public void ResolveQuality_ZeroCrf_IsNoOp()
    {
        // 0 = "not configured by profile" — resolver must not emit anything.
        ResolvedCodec nvenc = ResolveH264("h264_nvenc", GpuVendor.Nvidia, RateControlMode.Cq);
        Dictionary<string, string> flags = [];

        int crf = EncoderArgumentResolver.ResolveQuality(0, nvenc, flags);

        crf.Should().Be(0);
        flags.Should().BeEmpty();
    }

    [Fact]
    public void ResolveQuality_PreservesPreExistingVendorFlags()
    {
        // PlanStage seeds extraFlags with encoder.VendorSpecificFlags BEFORE calling
        // ResolveQuality. The resolver must add to the dict, not replace it —
        // otherwise HEVC videotoolbox loses its required -tag:v hvc1 and Apple
        // clients stop decoding, and AMF loses -usage transcoding and switches
        // to the wrong ratecontrol profile.
        ResolvedCodec amf = ResolveH264("h264_amf", GpuVendor.Amd, RateControlMode.Cqp);
        Dictionary<string, string> flags = new(amf.EncoderInfo.VendorSpecificFlags);
        flags["-usage"].Should().Be("transcoding");

        EncoderArgumentResolver.ResolveQuality(25, amf, flags);

        flags["-usage"].Should().Be("transcoding", "vendor flags must survive ResolveQuality");
        flags["-rc"].Should().Be("cqp");
        flags["-qp"].Should().Be("25");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // HEVC-specific — videotoolbox -tag:v hvc1 is mandatory for Apple playback
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HevcVideoToolbox_HasMandatoryHvc1Tag()
    {
        EncoderInfo vt = GetEncoder(VideoCodecType.H265, "hevc_videotoolbox");
        vt.VendorSpecificFlags.Should()
            .ContainKey("-tag:v", "HEVC in MP4 without hvc1 tag plays as video/octet on Apple");
        vt.VendorSpecificFlags["-tag:v"].Should().Be("hvc1");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ResolvePreset — per encoder family
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ResolvePreset_Software_UsesProfilePresetWhenSupported()
    {
        EncoderInfo libx264 = GetH264Encoder("libx264");
        string? preset = EncoderArgumentResolver.ResolvePreset("slow", libx264);
        preset.Should().Be("slow");
    }

    [Fact]
    public void ResolvePreset_Nvenc_UnsupportedPresetFallsBackToMiddle()
    {
        EncoderInfo nvenc = GetH264Encoder("h264_nvenc");
        // "slow" isn't in NVENC's p1..p7 set — resolver must substitute.
        string? preset = EncoderArgumentResolver.ResolvePreset("slow", nvenc);
        preset.Should().Be("p4", "NVENC's 7-preset middle is p4");
    }

    [Fact]
    public void ResolvePreset_Vaapi_ReturnsNull()
    {
        // VAAPI has no preset concept — the driver doesn't accept -preset.
        EncoderInfo vaapi = GetH264Encoder("h264_vaapi");
        vaapi.Presets.Should().BeEmpty();

        string? preset = EncoderArgumentResolver.ResolvePreset("medium", vaapi);
        preset.Should().BeNull();
    }

    [Fact]
    public void ResolvePreset_VideoToolbox_ReturnsNull()
    {
        EncoderInfo vt = GetH264Encoder("h264_videotoolbox");
        vt.Presets.Should().BeEmpty();
        EncoderArgumentResolver.ResolvePreset("medium", vt).Should().BeNull();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ResolveProfile — fallback behavior
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ResolveProfile_UnsupportedProfile_FallsBackToFirst()
    {
        EncoderInfo nvenc = GetH264Encoder("h264_nvenc");
        // high10 isn't in NVENC's profile set — driver rejects it, so we fall back.
        string? profile = EncoderArgumentResolver.ResolveProfile("high10", nvenc);
        profile.Should().Be("baseline", "NVENC's first-declared profile is the safe fallback");
    }

    [Fact]
    public void ResolveProfile_SupportedProfile_PassedThrough()
    {
        EncoderInfo libx264 = GetH264Encoder("libx264");
        EncoderArgumentResolver.ResolveProfile("high10", libx264).Should().Be("high10");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ResolveDimensions — no-upscale, even height
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ResolveDimensions_DoesNotUpscaleBeyondSource()
    {
        VideoOutput profile = MakeVideoOutput(width: 1920);
        (int w, int h) = EncoderArgumentResolver.ResolveDimensions(profile, 1280, 720);
        w.Should().Be(1280, "upscaling is off by design");
    }

    [Fact]
    public void ResolveDimensions_NullWidth_KeepsSourceWidth()
    {
        // An archive preset re-encodes the codec without rescaling: null width
        // means "keep source width", never the 2-pixel-wide floor a literal
        // 0 used to collapse to.
        VideoOutput profile = MakeVideoOutput(width: null);
        (int w, int h) = EncoderArgumentResolver.ResolveDimensions(profile, 3840, 1606);
        w.Should().Be(3840, "null width means keep the source width");
        h.Should().Be(1606, "height derives from the source aspect ratio");
    }

    [Fact]
    public void ResolveDimensions_LegacyZeroWidth_KeepsSourceWidth()
    {
        // Presets persisted before this field went nullable stored Width: 0.
        // That must resolve exactly like null — keep the source width —
        // never the 2-pixel-wide floor 0 used to collapse to.
        VideoOutput profile = MakeVideoOutput(width: 0);
        (int w, int h) = EncoderArgumentResolver.ResolveDimensions(profile, 3840, 2160);
        w.Should().Be(3840, "legacy 0 width means keep the source width, same as null");
        h.Should().Be(2160);
    }

    [Fact]
    public void ResolveDimensions_ForcesEvenHeight()
    {
        VideoOutput profile = MakeVideoOutput(width: 853); // 853x480 → 853*(480/1920) = 213.25
        (int w, int h) = EncoderArgumentResolver.ResolveDimensions(profile, 1920, 480);
        (h % 2).Should().Be(0, "h.264/h.265 require even dimensions");
    }

    [Fact]
    public void ResolveDimensions_UsesExplicitHeightWhenProvided()
    {
        VideoOutput profile = MakeVideoOutput(width: 1280, height: 720);
        (int w, int h) = EncoderArgumentResolver.ResolveDimensions(profile, 1920, 1080);
        w.Should().Be(1280);
        h.Should().Be(720);
    }

    [Fact]
    public void ResolveDimensions_ZeroHeight_DerivesFromSourceAspect()
    {
        // A width-only ladder rung (e.g. a 4K profile) reaches the resolver with
        // Height == 0, not null: LadderRung.Height is a non-nullable int, so the
        // "derive me" null gets collapsed to 0 by `?? 0` when a VideoOutput is
        // turned into a rung and back. Zero must be treated like null — derive
        // from the source aspect ratio. Otherwise the variant is named and
        // advertised "3840x0" (RESOLUTION=3840x0) and players skip the rendition.
        VideoOutput profile = MakeVideoOutput(width: 3840, height: 0);
        (int w, int h) = EncoderArgumentResolver.ResolveDimensions(profile, 3840, 2160);
        w.Should().Be(3840);
        h.Should().Be(2160, "zero height means derive from source AR, never literal 0");
    }

    [Fact]
    public void ResolveDimensions_ZeroHeight_DerivesEvenHeightForScopeSource()
    {
        // 2.39:1 scope source (a "Marvel 4K" master is rarely 16:9): 3840-wide
        // output must derive an even height from the real AR, not 2160.
        VideoOutput profile = MakeVideoOutput(width: 3840, height: 0);
        (int w, int h) = EncoderArgumentResolver.ResolveDimensions(profile, 3840, 1606);
        w.Should().Be(3840);
        h.Should().Be(1606, "derived height tracks the source AR");
        (h % 2).Should().Be(0, "encoders require even dimensions");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, 0)]
    [InlineData(0, null)]
    [InlineData(0, 0)]
    public void ResolveDimensions_NoSourceWidthAndKeepSourceRung_ReturnsSmallestEvenFrame(
        int? width,
        int? height
    )
    {
        // A probe that reports no width (a broken or unusual stream) combined
        // with a keep-source rung leaves nothing to derive an aspect ratio from.
        // The resolver must fall through to its 2x2 floor, never divide by zero.
        VideoOutput profile = MakeVideoOutput(width, height);
        (int w, int h) = EncoderArgumentResolver.ResolveDimensions(profile, 0, 0);
        w.Should().Be(2, "no usable width collapses to the smallest legal even frame");
        h.Should()
            .Be(2, "no aspect ratio to derive from collapses to the smallest legal even frame");
    }

    [Fact]
    public void ResolveProfile_HighOnNumericVocabulary_MapsToHighNotFirstEntry()
    {
        // h264_videotoolbox exposes numeric profiles ("66"=Baseline, "77"=Main,
        // "100"=High). "high" must map to "100", never silently fall back to the
        // first entry "66" (Baseline) — that ran Baseline while the profile
        // asked for High.
        EncoderInfo videotoolbox = new(
            FfmpegName: "h264_videotoolbox",
            RequiredVendor: null,
            Presets: [],
            Profiles: ["66", "77", "100"],
            Levels: [],
            QualityRange: new(0, 100, 50),
            SupportedRateControl: [RateControlMode.QualityLevel],
            Supports10Bit: false,
            SupportsHdr: false,
            MaxConcurrentSessions: 3,
            PixelFormat10Bit: "yuv420p10le",
            VendorSpecificFlags: new()
        );

        EncoderArgumentResolver.ResolveProfile("high", videotoolbox).Should().Be("100");
        EncoderArgumentResolver.ResolveProfile("main", videotoolbox).Should().Be("77");
        EncoderArgumentResolver.ResolveProfile("baseline", videotoolbox).Should().Be("66");
    }

    [Fact]
    public void ResolveProfile_KnownVocabulary_PassesThroughVerbatim()
    {
        EncoderInfo libx264 = new(
            FfmpegName: "libx264",
            RequiredVendor: null,
            Presets: ["medium"],
            Profiles: ["baseline", "main", "high"],
            Levels: ["4.1"],
            QualityRange: new(0, 51, 23),
            SupportedRateControl: [RateControlMode.Crf],
            Supports10Bit: false,
            SupportsHdr: false,
            MaxConcurrentSessions: int.MaxValue,
            PixelFormat10Bit: "yuv420p10le",
            VendorSpecificFlags: new()
        );

        EncoderArgumentResolver.ResolveProfile("high", libx264).Should().Be("high");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ResolveProfile — bit-depth coupling (10-bit must not emit an 8-bit profile)
    // ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("high")]
    [InlineData("main")]
    [InlineData("baseline")]
    public void ResolveProfile_H264_TenBit_PromotesEightBitTierToHigh10(string requested)
    {
        // libx264 with a 10-bit pixel format rejects an 8-bit-only profile
        // string ("high profile doesn't support a bit depth of 10"). H.264's
        // ONLY 10-bit profile is high10 (there is no h264 "main10"), so every
        // 8-bit tier promotes to high10 — the profile libx264 actually accepts.
        EncoderInfo libx264 = GetH264Encoder("libx264");
        EncoderArgumentResolver
            .ResolveProfile(requested, libx264, finalBitDepth: 10)
            .Should()
            .Be("high10");
    }

    [Fact]
    public void ResolveProfile_H264_TenBit_KeepsAlreadyTenBitProfile()
    {
        EncoderInfo libx264 = GetH264Encoder("libx264");
        EncoderArgumentResolver
            .ResolveProfile("high10", libx264, finalBitDepth: 10)
            .Should()
            .Be("high10");
    }

    [Fact]
    public void ResolveProfile_H264_EightBit_LeavesEightBitTierUntouched()
    {
        // The promotion must never fire at 8-bit — "high" stays "high".
        EncoderInfo libx264 = GetH264Encoder("libx264");
        EncoderArgumentResolver
            .ResolveProfile("high", libx264, finalBitDepth: 8)
            .Should()
            .Be("high");
    }

    [Fact]
    public void ResolveProfile_H265_TenBit_PromotesMainToMain10()
    {
        EncoderInfo libx265 = GetEncoder(VideoCodecType.H265, "libx265");
        EncoderArgumentResolver
            .ResolveProfile("main", libx265, finalBitDepth: 10)
            .Should()
            .Be("main10");
    }

    [Fact]
    public void ResolveProfile_H264_TenBit_NullProfile_FallsBackToTenBitCapable()
    {
        // A null (Auto) request at 10-bit must not fall back to Profiles[0]
        // ("baseline", 8-bit only) — the fallback itself has to be 10-bit-capable.
        EncoderInfo libx264 = GetH264Encoder("libx264");
        string? resolved = EncoderArgumentResolver.ResolveProfile(null, libx264, finalBitDepth: 10);
        resolved.Should().BeOneOf("high10", "high422", "high444p");
    }

    [Fact]
    public void ResolveProfile_Vp9_TenBit_DoesNotApplyH26xPromotion()
    {
        // VP9 numeric profiles mean chroma/bit-depth, NOT an H.264 tier. The
        // H26x promotion must not touch them — "main" is not VP9 vocabulary, so
        // it falls back to the encoder's own first profile, never "main10".
        EncoderInfo vpx = GetEncoder(VideoCodecType.Vp9, "libvpx-vp9");
        EncoderArgumentResolver
            .ResolveProfile("main", vpx, finalBitDepth: 10)
            .Should()
            .Be("0", "VP9 falls back to its own first profile, never an H26x sibling");
    }

    [Fact]
    public void ResolveDimensions_OddExplicitWidth_IsRoundedEven()
    {
        // 1921 passes validation (Width > 0) but reaches scale=1921:-2, which
        // libx264 rejects ("width not divisible by 2"). Width must be evened.
        VideoOutput profile = MakeVideoOutput(width: 1921);
        (int w, int _) = EncoderArgumentResolver.ResolveDimensions(profile, 3840, 2160);
        (w % 2).Should().Be(0, "an odd luma width aborts the encode");
        w.Should().Be(1920, "round DOWN so the result never exceeds the request/source");
    }

    [Fact]
    public void ResolveDimensions_OddManualRungWidth_IsRoundedEven()
    {
        // Manual ladder rungs carry hand-authored widths (e.g. 853 for 480p);
        // an odd rung width hits the same scale=W:-2 abort as an odd profile width.
        VideoOutput rung = MakeVideoOutput(width: 853, height: 480);
        (int w, int h) = EncoderArgumentResolver.ResolveDimensions(rung, 1920, 1080);
        (w % 2).Should().Be(0);
        (h % 2).Should().Be(0);
        w.Should().Be(852);
    }

    [Fact]
    public void ResolveDimensions_ZeroSourceWidth_DoesNotCollapseHeightToZero()
    {
        // A corrupt/partial probe can report sourceWidth = 0. With a derived
        // (null) height the old code produced height 0 → RESOLUTION=WIDTHx0 in
        // the HLS master. Height must fall back to a real value.
        VideoOutput profile = MakeVideoOutput(width: 1920, height: null);
        (int _, int h) = EncoderArgumentResolver.ResolveDimensions(profile, 0, 0);
        h.Should().BeGreaterThan(0, "a zero-height variant is skipped by every player");
        (h % 2).Should().Be(0);
    }

    [Fact]
    public void ResolveDimensions_ExplicitHeightAboveSource_IsClampedNoUpscale()
    {
        // Height=2160 on a 1280x720 source passed the width-only upscale guard
        // and reached scale_cuda=1280:2160 — a stretched upscale. Explicit
        // height must clamp to the source just like width does.
        VideoOutput profile = MakeVideoOutput(width: 1280, height: 2160);
        (int _, int h) = EncoderArgumentResolver.ResolveDimensions(profile, 1280, 720);
        h.Should().Be(720, "explicit height must not upscale beyond source");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static EncoderInfo GetH264Encoder(string ffmpegName) =>
        GetEncoder(VideoCodecType.H264, ffmpegName);

    private static EncoderInfo GetEncoder(VideoCodecType codec, string ffmpegName)
    {
        foreach ((VideoCodecType c, EncoderInfo encoder) in Registry.EnumerateVideoEncoders())
        {
            if (c == codec && encoder.FfmpegName == ffmpegName)
                return encoder;
        }
        throw new InvalidOperationException($"Encoder {ffmpegName} not registered for {codec}");
    }

    private static ResolvedCodec ResolveH264(
        string ffmpegName,
        GpuVendor? vendor,
        RateControlMode defaultRateControl
    )
    {
        EncoderInfo encoder = GetH264Encoder(ffmpegName);
        GpuDevice? device = vendor is null
            ? null
            : new GpuDevice(
                Vendor: vendor.Value,
                Name: $"Test {vendor.Value}",
                VramMb: 16_384,
                MaxEncoderSessions: 12,
                SupportedCodecs: [VideoCodecType.H264]
            );
        return new(ffmpegName, encoder, device, defaultRateControl);
    }

    private static VideoOutput MakeVideoOutput(int? width, int? height = null) =>
        new(
            Policy: StreamPolicy.Transcode,
            Codec: VideoCodecType.H264,
            Width: width,
            Height: height,
            RateControl: V2RateControlMode.Crf,
            Crf: 22,
            BitrateKbps: 4000,
            MaxBitrateKbps: null,
            BufferSizeKbps: null,
            Preset: "medium",
            CodecProfile: CodecProfile.High,
            Level: null,
            Tune: null,
            BitDepth: 8,
            PixelFormat: null,
            KeyframeIntervalSeconds: 2,
            ConvertHdrToSdr: false,
            SegmentNameTemplate: ":type:_:framesize:_:colorrange:/:type:_:framesize:_:colorrange:",
            PlaylistNameTemplate: ":type:_:framesize:_:colorrange:/:type:_:framesize:_:colorrange:"
        );
}
