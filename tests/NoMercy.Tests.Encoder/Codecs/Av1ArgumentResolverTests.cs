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

namespace NoMercy.Tests.Encoder.Codecs;

/// <summary>
/// AV1-specific resolver behavior. AV1 is the minefield of the codec
/// lineup because encoder families disagree on the fundamental shape of
/// the quality space:
///
///   libsvtav1 / libaom-av1 / av1_nvenc / av1_qsv : 0-51 (ish) / 0-63 / 0-51 / 1-51
///   av1_amf / av1_vaapi / librav1e              : 0-255 (!)
///
/// Set CRF 22 on a profile and the fixed "Default" quality is *relative*
/// to the encoder's range. Users who configure profiles in libsvtav1
/// terms (Crf=35 = balanced) will get near-lossless output if the
/// resolver picks av1_amf (QP 35 out of 255). These tests pin the
/// ranges so any future change to a definition breaks loudly.
///
/// Also pins the Apple omission: av1_videotoolbox does not exist.
/// Apple Silicon decodes AV1 in hardware but can't encode it, so the
/// codec definition must not declare one — otherwise the resolver
/// happily picks a phantom encoder and ffmpeg fails at runtime.
/// </summary>
public class Av1ArgumentResolverTests
{
    private static readonly CodecRegistry Registry = new();

    [Fact]
    public void LibSvtAv1_HasQualityRange_0To63()
    {
        EncoderInfo svt = Get("libsvtav1");
        svt.QualityRange.Min.Should().Be(0);
        svt.QualityRange.Max.Should().Be(63, "SVT-AV1 CRF caps at 63, not 51 like H264");
    }

    [Fact]
    public void LibSvtAv1_Supports10BitAndHdr()
    {
        EncoderInfo svt = Get("libsvtav1");
        svt.Supports10Bit.Should().BeTrue();
        svt.SupportsHdr.Should().BeTrue();
    }

    [Fact]
    public void LibAomAv1_PresetsMapToCpuUsed_0To8()
    {
        // libaom-av1's "preset" is actually cpu-used (0=slowest, 8=fastest).
        // ResolvePreset must pass "6" through unmodified.
        EncoderInfo aom = Get("libaom-av1");
        aom.Presets.Should().BeEquivalentTo("0", "1", "2", "3", "4", "5", "6", "7", "8");
        EncoderArgumentResolver.ResolvePreset("6", aom).Should().Be("6");
    }

    [Fact]
    public void LibRav1e_UsesQpRange_0To255_NotCrf()
    {
        // Rav1e's quality range is 0-255, and it only supports CQP + VBR —
        // NOT CRF. A profile configured with "Crf=22" (H264-style) would
        // silently map to QP 22/255 = near-lossless. The definition pins
        // the upper bound so any drift is caught.
        EncoderInfo rav1e = Get("librav1e");
        rav1e.QualityRange.Max.Should().Be(255);
        rav1e.SupportedRateControl.Should().NotContain(RateControlMode.Crf);
        rav1e.SupportedRateControl.Should().Contain(RateControlMode.Cqp);
    }

    [Fact]
    public void Av1Nvenc_MapsCrfToVbrCq_EmitsTranslatedValueAsIs()
    {
        // PlanStage translates the profile CRF (35 on the 0-63 AV1 scale) to
        // av1_nvenc's 0-51 CQ exactly once via NvencQualityScaler (= 28). The
        // resolver must emit that value unchanged — scaling it a second time
        // as if it were still a 0-63 value produced -cq 23.
        ResolvedCodec nvenc = Resolve("av1_nvenc", GpuVendor.Nvidia, RateControlMode.Cq);
        Dictionary<string, string> flags = [];

        EncoderArgumentResolver.ResolveQuality(28, nvenc, flags);

        flags["-rc"].Should().Be("vbr");
        flags["-cq"].Should().Be("28", "the scaler's value is already on the 0-51 CQ scale");
    }

    [Fact]
    public void Av1Amf_UsesFullQpRange_0To255()
    {
        // AMF AV1 QP is 0-255 even though H264/HEVC AMF use 0-51.
        // Critical to get right — CRF 35 passed naively becomes
        // -qp 35 (near-lossless) on av1_amf.
        EncoderInfo amf = Get("av1_amf");
        amf.QualityRange.Max.Should().Be(255, "AMD AV1 QP range is 0-255, NOT 0-51");
    }

    [Fact]
    public void Av1Amf_EmitsTranslatedQpAsIs()
    {
        // PlanStage translates CRF 35 (0-63 AV1 scale) to QP 142 (0-255 AMF
        // scale) exactly once via AmfAv1QualityScaler. Scaling 142 a second
        // time as if it were a 0-63 value produced 574, clamped to -qp 255 —
        // the worst quality AMF has.
        ResolvedCodec amf = Resolve("av1_amf", GpuVendor.Amd, RateControlMode.Cqp);
        Dictionary<string, string> flags = [];

        EncoderArgumentResolver.ResolveQuality(142, amf, flags);

        flags["-rc"].Should().Be("cqp");
        flags["-qp"].Should().Be("142");
    }

    [Fact]
    public void Av1Qsv_QualityStartsAt1_AndLacks10Bit()
    {
        EncoderInfo qsv = Get("av1_qsv");
        qsv.QualityRange.Min.Should().Be(1);
        qsv.Supports10Bit.Should().BeFalse();
    }

    [Fact]
    public void Av1Vaapi_HasNoPresets_FullQpRange()
    {
        EncoderInfo vaapi = Get("av1_vaapi");
        vaapi.Presets.Should().BeEmpty();
        vaapi.QualityRange.Max.Should().Be(255);
    }

    [Fact]
    public void Av1VideoToolbox_DoesNotExist()
    {
        // Apple Silicon decodes AV1 but can't encode. Resolver must never
        // find an av1_videotoolbox entry — otherwise ForceHardware on a
        // Mac would silently pick a phantom encoder.
        EncoderInfo[] av1Encoders =
        [
            .. Registry
                .EnumerateVideoEncoders()
                .Where(t => t.CodecType == VideoCodecType.Av1)
                .Select(t => t.Encoder),
        ];

        av1Encoders.Should().NotContain(e => e.FfmpegName == "av1_videotoolbox");
        av1Encoders.Should().NotContain(e => e.RequiredVendor == GpuVendor.Apple);
    }

    private static EncoderInfo Get(string ffmpegName)
    {
        foreach ((VideoCodecType c, EncoderInfo encoder) in Registry.EnumerateVideoEncoders())
        {
            if (c == VideoCodecType.Av1 && encoder.FfmpegName == ffmpegName)
                return encoder;
        }
        throw new InvalidOperationException($"AV1 encoder {ffmpegName} not registered");
    }

    private static ResolvedCodec Resolve(
        string ffmpegName,
        GpuVendor? vendor,
        RateControlMode defaultRateControl
    )
    {
        EncoderInfo encoder = Get(ffmpegName);
        GpuDevice? device = vendor is null
            ? null
            : new GpuDevice(
                Vendor: vendor.Value,
                Name: $"Test {vendor.Value}",
                VramMb: 16_384,
                MaxEncoderSessions: 12,
                SupportedCodecs: [VideoCodecType.Av1]
            );
        return new(ffmpegName, encoder, device, defaultRateControl);
    }
}
