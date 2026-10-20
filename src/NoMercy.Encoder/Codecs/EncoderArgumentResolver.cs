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

using NoMercy.Encoder.Profiles;

namespace NoMercy.Encoder.Codecs;

/// <summary>
/// Maps profile-level encoding parameters (preset, profile, CRF) to the correct
/// FFmpeg arguments for the resolved encoder. Profiles are written in libx264 terms
/// (e.g. preset=fast, profile=high, crf=22) but the actual encoder may be NVENC,
/// AMF, QSV, VAAPI, or VideoToolbox — each with different flag names and value sets.
/// </summary>
public static class EncoderArgumentResolver
{
    /// <summary>
    /// Returns the validated preset for the target encoder.
    /// Falls back to the encoder's middle preset if the profile preset is unsupported.
    /// </summary>
    public static string? ResolvePreset(string? profilePreset, EncoderInfo encoder)
    {
        if (encoder.Presets.Length == 0)
            return null; // Encoder has no preset concept (e.g. VAAPI)

        if (profilePreset is not null && encoder.Presets.Contains(profilePreset))
            return profilePreset;

        // Default to middle preset (balanced speed/quality)
        return encoder.Presets[encoder.Presets.Length / 2];
    }

    /// <summary>
    /// Returns the validated profile for the target encoder, honouring the final
    /// output bit depth.
    ///
    /// Profiles are written in libx264 vocabulary (baseline / main / high /
    /// high10). Some encoders use a disjoint vocabulary — h264_videotoolbox
    /// exposes numeric profiles ("66" = Baseline, "77" = Main, "100" = High).
    /// A blind fall-back to <c>Profiles[0]</c> silently downgrades every
    /// non-matching request to the encoder's first (usually weakest) profile —
    /// e.g. "high" on videotoolbox became "66" (Baseline), so the encode ran
    /// Baseline while the profile asked for High. Map by SEMANTICS first so the
    /// requested tier is honoured; only then fall back.
    ///
    /// Bit-depth coupling: an 8-bit-only H.264/HEVC profile string ("high",
    /// "main", "baseline") emitted next to a 10-bit pixel format makes the
    /// encoder abort — libx264 rejects "-profile:v high -pix_fmt yuv420p10le"
    /// with "high profile doesn't support a bit depth of 10". The bit depth and
    /// the profile are resolved in two different places (BitDepthPolicyResolver
    /// picks yuv420p10le, this resolver picks the profile), so they MUST be
    /// coupled here: when <paramref name="finalBitDepth"/> is 10, an 8-bit tier
    /// is promoted to its 10-bit sibling ("high"/"main" -> "high10"/"main10").
    /// Every encoder whose <c>Supports10Bit</c> is true carries a 10-bit profile
    /// in its vocabulary, so an encoder that reaches here at 10-bit always has a
    /// target to promote to; encoders that lack one never emit a 10-bit pixel
    /// format (the resolver downgrades to 8-bit upstream), so the promotion is a
    /// no-op for them.
    /// </summary>
    public static string? ResolveProfile(
        string? profileValue,
        EncoderInfo encoder,
        int finalBitDepth
    )
    {
        if (encoder.Profiles.Length == 0)
            return null;

        // Promote an 8-bit tier to a 10-bit-capable profile BEFORE resolution so
        // the emitted profile string agrees with the 10-bit pixel format. The
        // 10-bit target is whatever THIS encoder advertises (libx264 -> "high10",
        // libx265 -> "main10"), never a fixed name — H.264 has no "main10". A
        // request that is already 10-bit-capable is left untouched.
        if (finalBitDepth >= 10 && IsH26xProfileVocabulary(encoder) && Is8BitOnlyTier(profileValue))
        {
            string? tenBit = encoder.Profiles.FirstOrDefault(IsTenBitProfile);
            if (tenBit is not null)
                return tenBit;
        }

        string? effectiveProfile = profileValue;

        if (effectiveProfile is null)
            return TenBitAwareFallback(encoder, finalBitDepth);

        if (encoder.Profiles.Contains(effectiveProfile))
            return effectiveProfile;

        // The H.264-tier aliases (baseline/main/high -> numeric idc) only make
        // sense for H.264/HEVC encoders whose vocabulary IS those numeric codes
        // (e.g. videotoolbox "66"/"77"/"100"). VP9/AV1 profiles are numeric too
        // but mean chroma/bit-depth, NOT an H.264 tier — cross-mapping "main" to
        // VP9 "1" (4:2:2) would be wrong. Only alias-map for H26x-vocabulary
        // encoders; everything else falls back to the safe first profile.
        if (IsH26xProfileVocabulary(encoder))
        {
            string[] aliases = ProfileAliases(effectiveProfile);
            foreach (string alias in aliases)
            {
                string? match = encoder.Profiles.FirstOrDefault(p =>
                    string.Equals(p, alias, StringComparison.OrdinalIgnoreCase)
                );
                if (match is not null)
                    return match;
            }
        }

        return TenBitAwareFallback(encoder, finalBitDepth);
    }

    /// <summary>
    /// Backwards-compatible overload for callers that resolve 8-bit output.
    /// New callers that know the final bit depth should use the three-argument
    /// form so a 10-bit output never gets an 8-bit-only profile string.
    /// </summary>
    public static string? ResolveProfile(string? profileValue, EncoderInfo encoder) =>
        ResolveProfile(profileValue, encoder, finalBitDepth: 8);

    /// <summary>
    /// True when the requested profile names an 8-bit-only H.264/HEVC tier that
    /// must be promoted for a 10-bit output. Already-10-bit tiers (high10,
    /// main10, …) and a null request (encoder default) are handled elsewhere.
    /// </summary>
    private static bool Is8BitOnlyTier(string? profileValue) =>
        profileValue?.ToLowerInvariant() switch
        {
            "baseline" or "constrained_baseline" or "constrained_high" or "main" or "high" => true,
            "66" or "77" or "100" => true, // videotoolbox H.264 numeric idc — all 8-bit
            _ => false,
        };

    /// <summary>
    /// The safe fallback profile when nothing matched. At 10-bit, prefer the
    /// first profile the encoder advertises that is 10-bit-capable so the
    /// fallback itself can never contradict the pixel format; otherwise the
    /// first advertised profile.
    /// </summary>
    private static string TenBitAwareFallback(EncoderInfo encoder, int finalBitDepth)
    {
        if (finalBitDepth >= 10)
        {
            string? tenBit = encoder.Profiles.FirstOrDefault(IsTenBitProfile);
            if (tenBit is not null)
                return tenBit;
        }

        return encoder.Profiles[0];
    }

    /// <summary>
    /// True when the profile name denotes a 10-bit-capable H.264/HEVC profile.
    /// Covers the libx264 ("high10"), libx265 ("main10"/"main12"/"main*-10")
    /// and videotoolbox-HEVC numeric ("2" = Main10) spellings.
    /// </summary>
    private static bool IsTenBitProfile(string profile)
    {
        string p = profile.ToLowerInvariant();
        return p.Contains("10") || p.Contains("12");
    }

    /// <summary>
    /// True when the encoder's profile vocabulary is the H.264/HEVC tier set
    /// (named "baseline/main/high" or the videotoolbox numeric profile_idc
    /// codes). VP9/AV1 encoders also expose numeric profiles, but those denote
    /// chroma/bit-depth rather than an H.264 tier, so the tier aliases must not
    /// be applied to them.
    /// </summary>
    private static bool IsH26xProfileVocabulary(EncoderInfo encoder)
    {
        string name = encoder.FfmpegName.ToLowerInvariant();
        if (name.Contains("vp9") || name.Contains("vpx") || name.Contains("av1"))
            return false;
        return name.Contains("264")
            || name.Contains("h264")
            || name.Contains("hevc")
            || name.Contains("265")
            || name.Contains("x264")
            || name.Contains("x265");
    }

    /// <summary>
    /// Alternate spellings / numeric equivalents of a libx264-vocabulary profile
    /// name across the encoders NoMercy targets, most-specific first. Numeric
    /// values are the H.264 profile_idc (videotoolbox) and the HEVC-videotoolbox
    /// codes ("1" = Main, "2" = Main10).
    /// </summary>
    private static string[] ProfileAliases(string profileValue) =>
        profileValue.ToLowerInvariant() switch
        {
            "baseline" or "constrained_baseline" => ["baseline", "constrained_baseline", "66"],
            "main" => ["main", "77", "1"],
            "high" => ["high", "100"],
            "high10" or "main10" => ["high10", "main10", "2"],
            _ => [],
        };

    /// <summary>
    /// Maps CRF to the correct rate control flag for the encoder.
    /// Software encoders use -crf. Hardware encoders use -cq (NVENC), -qp (AMF/QSV),
    /// -global_quality (Intel ICQ), or -q:v (VideoToolbox).
    ///
    /// Returns the CRF value to pass to OutputOptions (0 if moved to extraFlags)
    /// and populates extraFlags with the hardware-specific quality flag.
    /// </summary>
    public static int ResolveQuality(
        int profileCrf,
        ResolvedCodec resolved,
        Dictionary<string, string> extraFlags
    )
    {
        if (profileCrf <= 0)
            return 0;

        // Stream copy passthrough has no quality dial. Profiles that
        // accidentally carry a non-zero CRF on a Copy output must not have
        // it forwarded as -qp / -cq / -crf — `ffmpeg -c:v copy` rejects
        // every quality flag and refuses to start the encode.
        if (resolved.FfmpegEncoderName == "copy")
            return 0;

        bool supportsCrf = resolved.EncoderInfo.SupportedRateControl.Contains(RateControlMode.Crf);
        if (supportsCrf)
            return profileCrf; // Software encoder — use -crf as-is

        // Hardware encoder — map to the correct quality flag with rate control mode.
        // Scale the profile's CRF value into the encoder's native quality range.
        // Without this, a profile written in libsvtav1 terms (Crf=35 on a 0-63 scale)
        // would reach av1_amf as "-qp 35" on its 0-255 scale (near-lossless) —
        // same CRF number, wildly different output size.
        int scaledQuality = ScaleQualityToEncoder(profileCrf, resolved.EncoderInfo);
        string qualityString = scaledQuality.ToString();

        switch (resolved.DefaultRateControl)
        {
            case RateControlMode.Cq:
                // NVENC: -rc vbr -cq VALUE
                extraFlags["-rc"] = "vbr";
                extraFlags["-cq"] = qualityString;
                break;
            case RateControlMode.Icq:
                // Intel QSV: -global_quality VALUE
                extraFlags["-global_quality"] = qualityString;
                break;
            case RateControlMode.QualityLevel:
                // VideoToolbox: -q:v VALUE
                extraFlags["-q:v"] = qualityString;
                break;
            default:
                // AMF/VAAPI: -rc cqp -qp VALUE
                extraFlags["-rc"] = "cqp";
                extraFlags["-qp"] = qualityString;
                break;
        }
        return 0; // Don't emit -crf
    }

    /// <summary>
    /// Scales a profile-level CRF value (always in "software-encoder" units for
    /// the codec) into the target encoder's native quality range. Proportional
    /// linear mapping — perceptually it's a rough approximation (CRF vs QP
    /// curves differ per encoder), but orders of magnitude closer than passing
    /// raw values. Clamped to the encoder's [Min, Max] so the emitted value is
    /// always accepted by ffmpeg.
    /// </summary>
    internal static int ScaleQualityToEncoder(int profileCrf, EncoderInfo encoder)
    {
        QualityRange range = encoder.QualityRange;

        // Reference ranges — the "software encoder" scale the profile was written in.
        // If the target encoder shares the same max, pass through to avoid
        // floating-point drift for the common case.
        int referenceMax = InferReferenceMax(encoder);
        if (range.Max == referenceMax)
            return Math.Clamp(profileCrf, range.Min, range.Max);

        double ratio = (double)profileCrf / referenceMax;
        int scaled = (int)Math.Round(ratio * range.Max);
        return Math.Clamp(scaled, Math.Max(1, range.Min), range.Max);
    }

    /// <summary>
    /// Returns the "reference" quality max for the codec family the encoder
    /// belongs to. H264 / HEVC / most QSV variants use 0-51. AV1 / VP9
    /// software uses 0-63. The profile is written in these reference units
    /// so scaling target the same point on the quality curve.
    /// </summary>
    private static int InferReferenceMax(EncoderInfo encoder)
    {
        // Heuristic based on encoder name — the codec family determines the
        // reference scale. Avoids taking a CodecRegistry dependency here.
        string name = encoder.FfmpegName.ToLowerInvariant();
        if (name.Contains("av1") || name.StartsWith("libsvtav1") || name.StartsWith("libaom"))
            return 63;
        if (name.Contains("vp9") || name.Contains("libvpx"))
            return 63;
        // H264, HEVC, and anything else — 0-51 reference.
        return 51;
    }

    /// <summary>
    /// Resolves output dimensions. Never upscales beyond source. Ensures even height.
    /// </summary>
    public static (int width, int height) ResolveDimensions(
        VideoOutput profile,
        int sourceWidth,
        int sourceHeight
    )
    {
        // A valid source width is required to reason about aspect ratio and to
        // clamp against upscaling. When the probe failed to report it, fall back
        // to the profile's own width (or 0 when the profile also asks to keep
        // source) so downstream never derives a zero height.
        int effectiveSourceWidth = sourceWidth > 0 ? sourceWidth : profile.Width ?? 0;
        int effectiveSourceHeight =
            sourceHeight > 0 ? sourceHeight
            : profile.Height is int h and > 0 ? h
            : effectiveSourceWidth * 9 / 16;

        // A null width — or a legacy persisted 0 from before this field went
        // nullable — means "keep source width": never rescale the width axis.
        // Only a positive width clamps against the source to prevent upscaling.
        int outputWidth = profile.Width is int w and > 0
            ? Math.Min(w, effectiveSourceWidth)
            : effectiveSourceWidth;

        // Height <= 0 means "derive from source AR" just like null: ladder rungs
        // carry a non-nullable int, so an upstream null collapses to 0 — a literal
        // 0 here would name the variant "WIDTHx0" and advertise RESOLUTION=WIDTHx0,
        // which players skip. An explicit height is clamped to the source so a
        // profile can never request an upscale ffmpeg would happily honor. With
        // no usable width at all (probe reported 0, rung keeps source) there is
        // no aspect ratio to derive from; leave 0 for the floor below.
        int outputHeight =
            profile.Height is int explicitHeight and > 0
                ? Math.Min(explicitHeight, effectiveSourceHeight)
            : effectiveSourceWidth > 0 ? outputWidth * effectiveSourceHeight / effectiveSourceWidth
            : 0;

        // Encoders require even luma dimensions on the common 4:2:0 pixel
        // formats; an odd width or height makes ffmpeg abort ("not divisible
        // by 2"). Round DOWN so the result never exceeds the source.
        outputWidth -= outputWidth % 2;
        outputHeight -= outputHeight % 2;

        // Never collapse to a zero axis — the smallest legal even frame.
        if (outputWidth <= 0)
            outputWidth = 2;
        if (outputHeight <= 0)
            outputHeight = 2;

        return (outputWidth, outputHeight);
    }
}
