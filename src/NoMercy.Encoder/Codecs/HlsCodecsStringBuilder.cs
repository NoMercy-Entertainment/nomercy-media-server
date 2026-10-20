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

using System.Globalization;

namespace NoMercy.Encoder.Codecs;

/// <summary>
/// Produces spec-accurate CODECS attribute strings for HLS EXT-X-STREAM-INF
/// and EXT-X-MEDIA tags as defined in:
///   • RFC 6381 §3.3 — codec string format
///   • ISO 14496-15 Annex E — avc1 / hvc1 parameter encoding
///   • AV1 Codec ISO Media File Format Binding §5 — av01 parameter encoding
///   • MP4 Registration Authority — mp4a object type descriptors
/// </summary>
public static class HlsCodecsStringBuilder
{
    // -----------------------------------------------------------------------
    // Video
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns the avc1 codec string for H.264.
    /// Format: avc1.PPCCLL where PP=profile_idc (hex), CC=constraint flags
    /// (hex), LL=level_idc (hex).
    ///
    /// Profile IDC values:
    ///   66 (0x42) = Baseline
    ///   77 (0x4D) = Main
    ///   100 (0x64) = High
    ///   110 (0x6E) = High 10
    ///   122 (0x7A) = High 4:2:2
    ///   244 (0xF4) = High 4:4:4 Predictive
    ///
    /// Constraint byte: 0x00 for High/Main, 0x40 for Baseline (constrained).
    /// Level examples: "4.0" → 0x28 (40), "4.1" → 0x29, "5.0" → 0x32, "5.1" → 0x33.
    /// </summary>
    public static string ForH264(
        string? profile,
        string? level,
        int width = 0,
        int height = 0,
        double frameRate = 0
    )
    {
        byte profileIdc = NormalizeH264Profile(profile) switch
        {
            "baseline" => 0x42,
            "main" => 0x4D,
            "high10" => 0x6E,
            "high422" => 0x7A,
            "high444" => 0xF4,
            _ => 0x64, // high (default)
        };

        // Constraint byte: Baseline uses 0x40 (constrained_set1_flag),
        // all others use 0x00 for broadest player compat.
        byte constraintByte = profileIdc == 0x42 ? (byte)0x40 : (byte)0x00;

        // The advertised level must be at least the minimum the resolution +
        // frame rate actually require. A preset carrying "4.0" (or none, which
        // defaults to 4.0) on a 4K rung would emit avc1...28 (level 4.0) — a
        // level that cannot legally carry 4K, which players validating codec
        // support reject (hls.js: manifestIncompatibleCodecsError). Clamp UP.
        byte levelIdc = ParseH264Level(level);
        if (width > 0 && height > 0)
            levelIdc = Math.Max(levelIdc, MinH264LevelIdc(width, height, frameRate));

        return $"avc1.{profileIdc:X2}{constraintByte:X2}{levelIdc:X2}";
    }

    /// <summary>
    /// Returns the hvc1 codec string for HEVC/H.265.
    /// Format: hvc1.P.CCC.LLL.BB where:
    ///   P   = general_profile_space (empty) + general_profile_idc (1 or 2)
    ///   CCC = general_profile_compatibility_flags (hex, 32-bit, e.g. 4 → "4")
    ///   LLL = tier ('L' or 'H') + general_level_idc (integer * 3 for 30 fps base)
    ///   BB  = general_constraint_indicator_flags (hex bytes, trailing zeros dropped)
    ///
    /// Common values for HLS delivery:
    ///   SDR Main (profile 1, compat 0x60000000 → "6"): hvc1.1.6.L93.B0
    ///   HDR Main10 (profile 2, compat 0x40000000 → "4"): hvc1.2.4.L120.B0
    /// </summary>
    public static string ForHevc(
        string? profile,
        string? level,
        bool tenBit,
        int width = 0,
        int height = 0,
        double frameRate = 0,
        string? pixelFormat = null
    )
    {
        // A rendition whose chroma is not 4:2:0 is Range Extensions (profile 4),
        // not Main/Main10 — 4:4:4 and 4:2:2 live only there. Advertising such a
        // stream as Main10 is what let a phone select a rendition its decoder
        // cannot open: the codec string said a profile every handset supports,
        // the bytes were RExt, and the failure surfaced as a dead player rather
        // than as the variant being filtered out of the master.
        bool rangeExtensions = IsNonFourTwoZeroChroma(pixelFormat);

        // Profile
        int profileIdc =
            rangeExtensions ? 4
            : tenBit ? 2
            : 1; // 1=Main, 2=Main10, 4=RExt

        // Compatibility flags: Main=0x60000000, Main10=0x40000000,
        // RExt=0x08000000. Encoded as the leading non-zero hex word only.
        string compatFlags =
            rangeExtensions ? "10"
            : tenBit ? "4"
            : "6";

        // Level: spec uses level_idc = level × 30 (e.g. L4.0 → 120, L3.1 → 93).
        // ParseHevcLevel defaults to L4.0 (120), which cannot legally carry 4K —
        // a 4K rung advertising hvc1.2.4.L120.B0 is rejected by players that
        // validate the codec string. Clamp UP to the minimum the resolution +
        // frame rate require (a 3840×2160 rung resolves to at least L5.0 / 150).
        int levelIdc = ParseHevcLevel(level, tenBit);
        if (width > 0 && height > 0)
            levelIdc = Math.Max(levelIdc, MinHevcLevelIdc(width, height, frameRate));
        string levelStr = $"L{levelIdc}";

        // RExt carries its general_constraint_indicator_flags rather than the
        // bare B0 a Main/Main10 stream gets: 9C.08 is what a 4:4:4 10-bit
        // rendition reports, and players match on those bytes when deciding
        // whether they have a decoder at all.
        string constraintBytes = rangeExtensions ? "9C.08" : "B0";

        return $"hvc1.{profileIdc}.{compatFlags}.{levelStr}.{constraintBytes}";
    }

    /// <summary>
    /// Whether a pixel format names a chroma subsampling other than 4:2:0.
    /// An unknown or absent format is treated as 4:2:0 — the overwhelmingly
    /// common case, and claiming RExt for a Main10 stream would filter a
    /// rendition every device can actually play.
    /// </summary>
    private static bool IsNonFourTwoZeroChroma(string? pixelFormat)
    {
        if (string.IsNullOrWhiteSpace(pixelFormat))
            return false;

        string lower = pixelFormat.ToLowerInvariant();

        return lower.Contains("444") || lower.Contains("422");
    }

    /// <summary>
    /// Returns the av01 codec string for AV1.
    /// Format: av01.P.LLT.DD where:
    ///   P  = profile (0=Main, 1=High, 2=Professional)
    ///   LL = level index (two-digit, padded, e.g. "04" for level 4.0)
    ///   T  = tier ('M'=Main, 'H'=High)
    ///   DD = bit depth (08 or 10)
    ///
    /// Level index mapping (AV1 spec Table A.1):
    ///   2.0→00, 2.1→01, 3.0→04, 3.1→05, 4.0→08, 4.1→09,
    ///   5.0→12, 5.1→13, 5.2→14, 5.3→15, 6.0→16, 6.1→17, 6.2→18, 6.3→19
    /// </summary>
    public static string ForAv1(
        string? level,
        bool tenBit,
        int width = 0,
        int height = 0,
        double frameRate = 0
    )
    {
        int levelIndex = ParseAv1LevelIndex(level, tenBit);
        if (width > 0 && height > 0)
            levelIndex = Math.Max(levelIndex, MinAv1LevelIndex(width, height, frameRate));
        string levelStr = levelIndex.ToString("D2");
        string bitDepth = tenBit ? "10" : "08";
        return $"av01.0.{levelStr}M.{bitDepth}";
    }

    // -----------------------------------------------------------------------
    // Audio
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns mp4a.40.2 for AAC-LC (ISO 14496-3 AudioObjectType=2).
    /// </summary>
    public static string ForAacLc() => "mp4a.40.2";

    /// <summary>
    /// Returns mp4a.40.5 for HE-AAC / AAC+ (SBR, AudioObjectType=5).
    /// </summary>
    public static string ForHeAac() => "mp4a.40.5";

    /// <summary>
    /// Returns ac-3 for Dolby Digital (AC-3).
    /// </summary>
    public static string ForAc3() => "ac-3";

    /// <summary>
    /// Returns ec-3 for Dolby Digital Plus (E-AC-3).
    /// </summary>
    public static string ForEac3() => "ec-3";

    // -----------------------------------------------------------------------
    // Encoder-name dispatch helpers (used by PlaylistGenerator)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Derives the CODECS string from FFmpeg encoder name + plan metadata.
    /// Covers all video codecs the encoder supports. Returns null for stream
    /// copy — the actual codec is whatever the source is, and advertising an
    /// assumed H.264 lies to the player (some, e.g. hls.js, hard-reject the
    /// master with manifestIncompatibleCodecsError when the CODECS value
    /// doesn't match the real stream). Omitting the attribute is spec-legal;
    /// RFC 8216 §4.4.6.2 has clients probe the stream directly when absent.
    /// </summary>
    public static string? VideoCodecString(
        string encoderName,
        string? profile,
        string? level,
        bool tenBit,
        int width = 0,
        int height = 0,
        double frameRate = 0,
        string? pixelFormat = null
    )
    {
        VideoCodecType? family = CodecFamilyClassifier.ClassifyVideo(encoderName);

        return family switch
        {
            VideoCodecType.H264 => ForH264(profile, level, width, height, frameRate),
            VideoCodecType.H265 => ForHevc(
                profile,
                level,
                tenBit,
                width,
                height,
                frameRate,
                pixelFormat
            ),
            VideoCodecType.Av1 => ForAv1(level, tenBit, width, height, frameRate),
            // VP9 — no RFC 6381 standardised short-form; use vp09 signaling
            VideoCodecType.Vp9 => $"vp09.00.41.{(tenBit ? "10" : "08")}",
            VideoCodecType.Copy => null,
            // Fallback: treat as H.264 High 4.0
            _ => ForH264(profile ?? "high", level ?? "4.0", width, height, frameRate),
        };
    }

    /// <summary>
    /// Derives the CODECS string from FFmpeg encoder name for audio. Returns
    /// null for stream copy — see <see cref="VideoCodecString"/> for why an
    /// assumed value is worse than omitting the attribute.
    /// </summary>
    public static string? AudioCodecString(string encoderName, bool heAac = false)
    {
        return encoderName.ToLowerInvariant() switch
        {
            "aac" or "libfdk_aac" => heAac ? ForHeAac() : ForAacLc(),
            "ac3" => ForAc3(),
            "eac3" => ForEac3(),
            "libopus" or "opus" => "opus",
            "copy" => null,
            _ => ForAacLc(),
        };
    }

    // -----------------------------------------------------------------------
    // Private parsing helpers
    // -----------------------------------------------------------------------

    private static string NormalizeH264Profile(string? profile)
    {
        if (string.IsNullOrEmpty(profile))
            return "high";

        return profile.ToLowerInvariant() switch
        {
            "baseline" or "constrained baseline" or "cb" => "baseline",
            "main" or "m" => "main",
            "high 10" or "high10" or "hi10p" => "high10",
            "high 4:2:2" or "high422" or "hi422p" => "high422",
            "high 4:4:4" or "high444" or "hi444pp" => "high444",
            _ => "high",
        };
    }

    private static byte ParseH264Level(string? level)
    {
        if (string.IsNullOrEmpty(level))
            return 0x28; // 4.0

        // The level table goes first: a whole number such as "4" is the major
        // level 4.0, and reading it as level_idc 4 would advertise level 0.4.
        byte? tabled = level switch
        {
            "1" or "1.0" => 10,
            "1.1" => 11,
            "1.2" => 12,
            "1.3" => 13,
            "2" or "2.0" => 20,
            "2.1" => 21,
            "2.2" => 22,
            "3" or "3.0" => 30,
            "3.1" => 31,
            "3.2" => 32,
            "4" or "4.0" => 40,
            "4.1" => 41,
            "4.2" => 42,
            "5" or "5.0" => 50,
            "5.1" => 51,
            "5.2" => 52,
            "6" or "6.0" => 60,
            "6.1" => 61,
            "6.2" => 62,
            _ => null,
        };
        if (tabled is byte tabledIdc)
            return tabledIdc;

        // level_idc-style input ("40", "41"). Only the range the table spans is
        // a level; anything larger would wrap in the byte cast.
        if (
            int.TryParse(level, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric)
            && numeric is >= 10 and <= 62
        )
            return (byte)numeric;

        return 40;
    }

    private static int ParseHevcLevel(string? level, bool tenBit)
    {
        // HEVC level_idc = level_value × 30
        // Common defaults: SDR → L3.1 (93), HDR10 → L4.0 (120)
        if (string.IsNullOrEmpty(level))
            return tenBit ? 120 : 93;

        return level switch
        {
            "1" or "1.0" => 30,
            "2" or "2.0" => 60,
            "2.1" => 63,
            "3" or "3.0" => 90,
            "3.1" => 93,
            "4" or "4.0" => 120,
            "4.1" => 123,
            "5" or "5.0" => 150,
            "5.1" => 153,
            "5.2" => 156,
            "6" or "6.0" => 180,
            "6.1" => 183,
            "6.2" => 186,
            _ => tenBit ? 120 : 93,
        };
    }

    // -----------------------------------------------------------------------
    // Resolution-derived minimum levels
    //
    // A codec level string advertises the decoder capability a stream needs.
    // When it is copied from the preset (which may be unset → a low default)
    // it can under-state what the actual resolution requires, producing an
    // illegal pairing (e.g. 4K at HEVC L4.0). These compute the LOWEST level
    // that legally carries width×height at frameRate, per each codec's level
    // table; ForXxx clamps the advertised level up to this floor.
    // -----------------------------------------------------------------------

    private static byte MinH264LevelIdc(int width, int height, double frameRate)
    {
        // H.264 Annex A: constrained by frame size (MaxFS) and macroblock rate
        // (MaxMBPS), both in 16×16 macroblocks. level_idc = level × 10.
        long frameMbs = (long)((width + 15) / 16) * ((height + 15) / 16);
        double mbRate = frameMbs * (frameRate > 0 ? frameRate : 30.0);

        (byte Idc, long MaxFs, double MaxMbps)[] levels =
        [
            (30, 1620, 40_500), // 3.0
            (31, 3600, 108_000), // 3.1
            (32, 5120, 216_000), // 3.2
            (40, 8192, 245_760), // 4.0
            (41, 8192, 245_760), // 4.1
            (42, 8704, 522_240), // 4.2
            (50, 22_080, 589_824), // 5.0
            (51, 36_864, 983_040), // 5.1
            (52, 36_864, 2_073_600), // 5.2
            (60, 139_264, 4_177_920), // 6.0
            (61, 139_264, 8_355_840), // 6.1
            (62, 139_264, 16_711_680), // 6.2
        ];

        foreach ((byte idc, long maxFs, double maxMbps) in levels)
            if (frameMbs <= maxFs && mbRate <= maxMbps)
                return idc;

        return 62;
    }

    private static int MinHevcLevelIdc(int width, int height, double frameRate)
    {
        // HEVC Annex A: constrained by luma picture size (MaxLumaPs, samples)
        // and luma sample rate (MaxLumaSr, samples/sec). level_idc = level × 30.
        long lumaPs = (long)width * height;
        double lumaSr = lumaPs * (frameRate > 0 ? frameRate : 30.0);

        (int Idc, long MaxPs, double MaxSr)[] levels =
        [
            (90, 552_960, 16_588_800), // 3.0
            (93, 983_040, 33_177_600), // 3.1
            (120, 2_228_224, 66_846_720), // 4.0
            (123, 2_228_224, 133_693_440), // 4.1
            (150, 8_912_896, 267_386_880), // 5.0
            (153, 8_912_896, 534_773_760), // 5.1
            (156, 8_912_896, 1_069_547_520), // 5.2
            (180, 35_651_584, 1_069_547_520), // 6.0
            (183, 35_651_584, 2_139_095_040), // 6.1
            (186, 35_651_584, 4_278_190_080), // 6.2
        ];

        foreach ((int idc, long maxPs, double maxSr) in levels)
            if (lumaPs <= maxPs && lumaSr <= maxSr)
                return idc;

        return 186;
    }

    private static int MinAv1LevelIndex(int width, int height, double frameRate)
    {
        // AV1 spec Table A.1: constrained by MaxPicSize (samples) and
        // MaxDisplayRate (samples/sec). Returns the level INDEX (not level×N).
        long picSize = (long)width * height;
        double displayRate = picSize * (frameRate > 0 ? frameRate : 30.0);

        (int Index, long MaxPic, double MaxRate)[] levels =
        [
            (4, 1_098_000, 19_975_680), // 3.0
            (5, 1_098_000, 31_950_720), // 3.1
            (8, 2_359_296, 70_778_880), // 4.0
            (9, 2_359_296, 141_557_760), // 4.1
            (12, 8_912_896, 267_386_880), // 5.0
            (13, 8_912_896, 534_773_760), // 5.1
            (14, 8_912_896, 1_069_547_520), // 5.2
            (16, 35_651_584, 1_069_547_520), // 6.0
            (17, 35_651_584, 2_139_095_040), // 6.1
            (18, 35_651_584, 4_278_190_080), // 6.2
        ];

        foreach ((int index, long maxPic, double maxRate) in levels)
            if (picSize <= maxPic && displayRate <= maxRate)
                return index;

        return 18;
    }

    private static int ParseAv1LevelIndex(string? level, bool tenBit = false)
    {
        // AV1 spec Table A.1 level index mapping.
        // Defaults are opinionated by bit depth: 10-bit content typically
        // targets 4K → level 5.3 (index 15); 8-bit defaults to 4.0 (index 8).
        if (string.IsNullOrEmpty(level))
            return tenBit ? 15 : 8;

        return level switch
        {
            "2.0" => 0,
            "2.1" => 1,
            "3.0" => 4,
            "3.1" => 5,
            "4.0" => 8,
            "4.1" => 9,
            "5.0" => 12,
            "5.1" => 13,
            "5.2" => 14,
            "5.3" => 15,
            "6.0" => 16,
            "6.1" => 17,
            "6.2" => 18,
            "6.3" => 19,
            _ => 8,
        };
    }
}
