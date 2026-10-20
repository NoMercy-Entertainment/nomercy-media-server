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

namespace NoMercy.Tests.Encoder.Codecs;

/// <summary>
/// HLS CODECS strings for EXT-X-STREAM-INF / EXT-X-MEDIA tags follow strict
/// RFC 6381 / ISO 14496-15 / AV1 ISOBMFF binding formats. Wrong characters
/// or wrong levels make players reject the variant entirely — every value
/// here is a contract with player implementations.
/// </summary>
public class HlsCodecsStringBuilderTests
{
    // ── H.264 (avc1.PPCCLL) ────────────────────────────────────────────────

    [Theory]
    [InlineData("baseline", "3.0", "avc1.42401E")] // PP=0x42, CC=0x40 (constraint), LL=0x1E (30)
    [InlineData("main", "4.0", "avc1.4D0028")] // PP=0x4D, CC=0x00, LL=0x28 (40)
    [InlineData("high", "4.1", "avc1.640029")] // PP=0x64, CC=0x00, LL=0x29 (41)
    [InlineData("high10", "5.0", "avc1.6E0032")] // PP=0x6E (High 10), LL=0x32 (50)
    [InlineData("high422", "5.1", "avc1.7A0033")] // PP=0x7A (High 4:2:2), LL=0x33 (51)
    [InlineData("high444", "5.2", "avc1.F40034")] // PP=0xF4 (High 4:4:4), LL=0x34 (52)
    public void ForH264_KnownProfileLevels(string profile, string level, string expected)
    {
        HlsCodecsStringBuilder.ForH264(profile, level).Should().Be(expected);
    }

    [Fact]
    public void ForH264_NullProfile_DefaultsToHigh()
    {
        HlsCodecsStringBuilder.ForH264(null, "4.0").Should().Be("avc1.640028");
    }

    [Fact]
    public void ForH264_NullLevel_DefaultsTo4_0()
    {
        HlsCodecsStringBuilder.ForH264("high", null).Should().Be("avc1.640028");
    }

    [Theory]
    [InlineData("constrained baseline", "avc1.42401E")] // Marketing-name alias
    [InlineData("hi10p", "avc1.6E001E")] // x264-style alias (10-bit)
    public void ForH264_AcceptsCommonAliases(string profile, string expectedPrefix)
    {
        HlsCodecsStringBuilder.ForH264(profile, "3.0").Should().Be(expectedPrefix);
    }

    // ── HEVC (hvc1.P.CC.LLL.B0) ────────────────────────────────────────────

    [Theory]
    [InlineData("3.1", false, "hvc1.1.6.L93.B0")] // SDR Main
    [InlineData("4.0", true, "hvc1.2.4.L120.B0")] // HDR Main10
    [InlineData("5.1", false, "hvc1.1.6.L153.B0")] // SDR Main, 4K
    [InlineData("5.1", true, "hvc1.2.4.L153.B0")] // HDR Main10, 4K
    public void ForHevc_KnownLevels(string level, bool tenBit, string expected)
    {
        HlsCodecsStringBuilder.ForHevc("main", level, tenBit).Should().Be(expected);
    }

    [Fact]
    public void ForHevc_NullLevel_DefaultsByBitDepth()
    {
        HlsCodecsStringBuilder.ForHevc("main", null, tenBit: false).Should().Be("hvc1.1.6.L93.B0");
        HlsCodecsStringBuilder.ForHevc("main", null, tenBit: true).Should().Be("hvc1.2.4.L120.B0");
    }

    /// <summary>
    /// A 4:4:4 or 4:2:2 rendition is Range Extensions, and no phone has a
    /// decoder for it. Advertised as Main10 it looks playable to every client,
    /// so the variant is selected and playback dies at the decoder instead of
    /// the rendition being filtered out of the master where it belongs.
    /// </summary>
    [Theory]
    [InlineData("yuv444p10le", "hvc1.4.10.L153.9C.08")]
    [InlineData("yuv422p10le", "hvc1.4.10.L153.9C.08")]
    [InlineData("yuv420p10le", "hvc1.2.4.L153.B0")]
    [InlineData(null, "hvc1.2.4.L153.B0")]
    public void ForHevc_NonFourTwoZeroChroma_IsRangeExtensions(string? pixelFormat, string expected)
    {
        HlsCodecsStringBuilder
            .ForHevc("main", "5.1", tenBit: true, pixelFormat: pixelFormat)
            .Should()
            .Be(expected);
    }

    // ── AV1 (av01.0.LLT.DD) ────────────────────────────────────────────────

    [Theory]
    [InlineData("4.0", false, "av01.0.08M.08")] // 1080p AV1 8-bit
    [InlineData("5.0", false, "av01.0.12M.08")] // 4K AV1 8-bit
    [InlineData("5.3", true, "av01.0.15M.10")] // 4K AV1 10-bit
    [InlineData("3.0", false, "av01.0.04M.08")] // 720p AV1 8-bit
    public void ForAv1_KnownLevels(string level, bool tenBit, string expected)
    {
        HlsCodecsStringBuilder.ForAv1(level, tenBit).Should().Be(expected);
    }

    [Fact]
    public void ForAv1_NullLevel_DefaultsByBitDepth()
    {
        HlsCodecsStringBuilder.ForAv1(null, tenBit: false).Should().Be("av01.0.08M.08");
        HlsCodecsStringBuilder.ForAv1(null, tenBit: true).Should().Be("av01.0.15M.10");
    }

    // ── Audio codec strings ────────────────────────────────────────────────

    [Theory]
    [InlineData("aac", false, "mp4a.40.2")] // AAC-LC
    [InlineData("aac", true, "mp4a.40.5")] // HE-AAC
    [InlineData("libfdk_aac", false, "mp4a.40.2")]
    [InlineData("libfdk_aac", true, "mp4a.40.5")]
    [InlineData("ac3", false, "ac-3")]
    [InlineData("eac3", false, "ec-3")]
    [InlineData("libopus", false, "opus")]
    [InlineData("opus", false, "opus")]
    [InlineData("unknown_codec", false, "mp4a.40.2")] // Defaults to AAC-LC
    public void AudioCodecString_KnownEncoders(string encoder, bool heAac, string expected)
    {
        HlsCodecsStringBuilder.AudioCodecString(encoder, heAac).Should().Be(expected);
    }

    [Fact]
    public void AudioCodecString_Copy_ReturnsNullInsteadOfLying()
    {
        // Same reasoning as VideoCodecString_Copy — the source audio codec
        // could be anything; defaulting to mp4a.40.2 (AAC-LC) misdeclares it.
        HlsCodecsStringBuilder.AudioCodecString("copy").Should().BeNull();
    }

    // ── VideoCodecString dispatch via classifier ────────────────────────────

    [Theory]
    [InlineData("libx264", "main", "4.0", false, "avc1.4D0028")]
    [InlineData("h264_nvenc", "high", "4.1", false, "avc1.640029")]
    [InlineData("libx265", "main", "3.1", false, "hvc1.1.6.L93.B0")]
    [InlineData("hevc_nvenc", "main10", "4.0", true, "hvc1.2.4.L120.B0")]
    [InlineData("libsvtav1", null, "5.0", false, "av01.0.12M.08")]
    [InlineData("av1_nvenc", null, "5.3", true, "av01.0.15M.10")]
    [InlineData("libvpx-vp9", null, null, false, "vp09.00.41.08")]
    [InlineData("vp9_qsv", null, null, true, "vp09.00.41.10")]
    public void VideoCodecString_ClassifiesEncoderCorrectly(
        string encoder,
        string? profile,
        string? level,
        bool tenBit,
        string expected
    )
    {
        HlsCodecsStringBuilder
            .VideoCodecString(encoder, profile, level, tenBit)
            .Should()
            .Be(expected);
    }

    [Fact]
    public void VideoCodecString_UnknownEncoder_FallsBackToH264HighL4_0()
    {
        HlsCodecsStringBuilder
            .VideoCodecString("totally_unknown_encoder", null, null, false)
            .Should()
            .Be("avc1.640028");
    }

    [Fact]
    public void VideoCodecString_Copy_ReturnsNullInsteadOfLying()
    {
        // "copy" passes the source stream through untouched — the real codec
        // could be HEVC, AV1, VP9, anything. Falling back to the H.264 default
        // (the pre-fix behavior) makes the master playlist advertise the
        // wrong codec, which some players (hls.js) hard-reject.
        HlsCodecsStringBuilder.VideoCodecString("copy", null, null, false).Should().BeNull();
    }

    [Theory]
    [InlineData("COPY")]
    [InlineData("Copy")]
    public void VideoCodecString_CopyCaseInsensitive_ReturnsNull(string encoder)
    {
        HlsCodecsStringBuilder.VideoCodecString(encoder, "high", "4.0", false).Should().BeNull();
    }

    // ── ParseH264Level numeric input ───────────────────────────────────────

    [Theory]
    [InlineData("40", "avc1.640028")] // "40" parses as numeric 40 = level 4.0
    [InlineData("51", "avc1.640033")] // "51" → 0x33 = level 5.1
    public void ForH264_AcceptsNumericLevelInput(string level, string expected)
    {
        HlsCodecsStringBuilder.ForH264("high", level).Should().Be(expected);
    }

    [Fact]
    public void ForH264_UnknownLevelString_DefaultsTo4_0()
    {
        // Garbage that fails numeric parse falls through the lookup default.
        HlsCodecsStringBuilder.ForH264("high", "garbage").Should().Be("avc1.640028");
    }

    [Theory]
    [InlineData("4", "avc1.640028")] // a whole number is the major level: 4 → 4.0 (0x28), not level_idc 4
    [InlineData("5", "avc1.640032")]
    [InlineData("3", "avc1.64001E")]
    [InlineData("41", "avc1.640029")] // level_idc-style input keeps working
    public void ForH264_WholeNumberLevel_MeansMajorLevel(string level, string expected)
    {
        HlsCodecsStringBuilder.ForH264("high", level).Should().Be(expected);
    }

    [Theory]
    [InlineData("256")] // would wrap to 0x00 in a byte cast
    [InlineData("300")]
    [InlineData("9")] // below the smallest level_idc (1.0 = 10)
    [InlineData("-40")]
    public void ForH264_NumberOutsideLevelIdcRange_DefaultsTo4_0(string level)
    {
        HlsCodecsStringBuilder.ForH264("high", level).Should().Be("avc1.640028");
    }

    // ── HEVC additional levels ──────────────────────────────────────────────

    [Theory]
    [InlineData("3", "hvc1.1.6.L90.B0")]
    [InlineData("4.1", "hvc1.1.6.L123.B0")]
    [InlineData("6.0", "hvc1.1.6.L180.B0")]
    [InlineData("6.2", "hvc1.1.6.L186.B0")]
    public void ForHevc_AdditionalLevels(string level, string expected)
    {
        HlsCodecsStringBuilder.ForHevc("main", level, tenBit: false).Should().Be(expected);
    }

    [Fact]
    public void ForHevc_UnknownLevel_DefaultsByBitDepth()
    {
        // Garbage level falls through the switch's `_ =>` arm.
        HlsCodecsStringBuilder
            .ForHevc("main", "weird", tenBit: false)
            .Should()
            .Be("hvc1.1.6.L93.B0");
        HlsCodecsStringBuilder
            .ForHevc("main", "weird", tenBit: true)
            .Should()
            .Be("hvc1.2.4.L120.B0");
    }

    // ── AV1 additional levels ──────────────────────────────────────────────

    [Theory]
    [InlineData("2.0", false, "av01.0.00M.08")]
    [InlineData("2.1", false, "av01.0.01M.08")]
    [InlineData("3.1", false, "av01.0.05M.08")]
    [InlineData("4.1", false, "av01.0.09M.08")]
    [InlineData("5.1", false, "av01.0.13M.08")]
    [InlineData("5.2", true, "av01.0.14M.10")]
    [InlineData("6.0", false, "av01.0.16M.08")]
    [InlineData("6.3", true, "av01.0.19M.10")]
    public void ForAv1_AdditionalLevels(string level, bool tenBit, string expected)
    {
        HlsCodecsStringBuilder.ForAv1(level, tenBit).Should().Be(expected);
    }

    [Fact]
    public void ForAv1_UnknownLevel_Defaults_AndBitDepthIndependent()
    {
        // Non-empty garbage level → switch falls through to index 8 (level 4.0)
        // regardless of bit depth. Only the null/empty case defaults to 15 for
        // 10-bit content — the lookup default is always 8.
        HlsCodecsStringBuilder.ForAv1("garbage", tenBit: false).Should().Be("av01.0.08M.08");
        HlsCodecsStringBuilder.ForAv1("garbage", tenBit: true).Should().Be("av01.0.08M.10");
    }

    // ── Audio constants ─────────────────────────────────────────────────────

    [Fact]
    public void AudioConstants_ReturnSpecExactStrings()
    {
        // Pin the exact strings — these are wire-format identifiers from
        // the MP4 Registration Authority. Any drift breaks player parsers
        // that string-match against well-known values.
        HlsCodecsStringBuilder.ForAacLc().Should().Be("mp4a.40.2");
        HlsCodecsStringBuilder.ForHeAac().Should().Be("mp4a.40.5");
        HlsCodecsStringBuilder.ForAc3().Should().Be("ac-3");
        HlsCodecsStringBuilder.ForEac3().Should().Be("ec-3");
    }

    [Fact]
    public void AudioCodecString_CaseInsensitiveEncoderName()
    {
        // Encoder names land here as-is from PlaylistGenerator; the matcher
        // must accept the canonical lowercase as well as upper/mixed case.
        HlsCodecsStringBuilder.AudioCodecString("AAC", false).Should().Be("mp4a.40.2");
        HlsCodecsStringBuilder.AudioCodecString("OPUS", false).Should().Be("opus");
        HlsCodecsStringBuilder.AudioCodecString("EAC3", false).Should().Be("ec-3");
    }

    // ── Resolution-derived level floor ──────────────────────────────────────
    //
    // The Punisher master advertised hvc1.2.4.L120.B0 (HEVC level 4.0) for the
    // 4K HDR rung — a level that legally tops out at 1080p. The level came
    // straight from the preset (or its null → L4.0 default) and was never
    // checked against the resolution. A player validating the codec string can
    // reject the variant. The advertised level must be clamped UP to the
    // minimum the resolution + frame rate actually require.

    [Fact]
    public void ForHevc_4KHdr_ClampsLevelUpFromPresetDefault()
    {
        // 3840×2160 @ 23.976 needs ≥ L5.0 (150). A preset saying "4.0" (120)
        // or nothing must NOT win — the emitted level rises to the 4K floor.
        HlsCodecsStringBuilder
            .ForHevc("main10", "4.0", tenBit: true, width: 3840, height: 2160, frameRate: 23.976)
            .Should()
            .Be("hvc1.2.4.L150.B0");

        HlsCodecsStringBuilder
            .ForHevc("main10", null, tenBit: true, width: 3840, height: 2160, frameRate: 23.976)
            .Should()
            .Be("hvc1.2.4.L150.B0");
    }

    [Fact]
    public void ForHevc_4KHighFrameRate_NeedsLevel5_1()
    {
        // 3840×2160 @ 60 exceeds L5.0's sample-rate ceiling → L5.1 (153).
        HlsCodecsStringBuilder
            .ForHevc("main10", "4.0", tenBit: true, width: 3840, height: 2160, frameRate: 60)
            .Should()
            .Be("hvc1.2.4.L153.B0");
    }

    [Fact]
    public void ForHevc_1080p_KeepsPresetLevelWhenResolutionAllows()
    {
        // 1080p genuinely fits L4.0 — the clamp is a floor, not an override, so
        // the correct low level is preserved.
        HlsCodecsStringBuilder
            .ForHevc("main10", "4.0", tenBit: true, width: 1920, height: 1080, frameRate: 23.976)
            .Should()
            .Be("hvc1.2.4.L120.B0");
    }

    [Fact]
    public void ForHevc_PresetAboveFloor_IsNotLowered()
    {
        // A preset that already over-states the level (5.2 on 1080p) is kept —
        // the clamp only raises, never lowers.
        HlsCodecsStringBuilder
            .ForHevc("main10", "5.2", tenBit: true, width: 1920, height: 1080, frameRate: 23.976)
            .Should()
            .Be("hvc1.2.4.L156.B0");
    }

    [Fact]
    public void ForH264_4K_ClampsToLevel5_1()
    {
        // 3840×2160 H.264 needs ≥ L5.1 (0x33) — L4.0 cannot carry the frame
        // size. Preset "4.0" must be raised.
        HlsCodecsStringBuilder
            .ForH264("high", "4.0", width: 3840, height: 2160, frameRate: 23.976)
            .Should()
            .Be("avc1.640033");
    }

    [Fact]
    public void ForAv1_4K8Bit_ClampsToLevel5_0()
    {
        // 3840×2160 @ 23.976 8-bit AV1 needs level index 12 (5.0). A preset
        // "4.0" (index 8) under-states it.
        HlsCodecsStringBuilder
            .ForAv1("4.0", tenBit: false, width: 3840, height: 2160, frameRate: 23.976)
            .Should()
            .Be("av01.0.12M.08");
    }

    [Fact]
    public void VideoCodecString_4KHevc_EmitsResolutionCorrectLevel()
    {
        // End-to-end through the classifier: the exact regression the Punisher
        // master hit — hevc_nvenc, 4K, preset level 4.0 → must NOT be L120.
        HlsCodecsStringBuilder
            .VideoCodecString(
                "hevc_nvenc",
                "main10",
                "4.0",
                tenBit: true,
                width: 3840,
                height: 2160,
                frameRate: 23.976
            )
            .Should()
            .Be("hvc1.2.4.L150.B0");
    }

    [Fact]
    public void VideoCodecString_NoResolution_LeavesLevelUnclamped()
    {
        // Callers that don't pass a resolution (width/height default 0) keep the
        // pre-clamp behavior — the floor only applies with real dimensions.
        HlsCodecsStringBuilder
            .VideoCodecString("hevc_nvenc", "main10", "4.0", tenBit: true)
            .Should()
            .Be("hvc1.2.4.L120.B0");
    }
}
