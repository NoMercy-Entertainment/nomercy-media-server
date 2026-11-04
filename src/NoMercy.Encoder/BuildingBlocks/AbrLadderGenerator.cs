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

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Profiles;

namespace NoMercy.Encoder.BuildingBlocks;

/// <summary>
/// Produces a standard streaming ABR ladder from source media.
///
/// Legacy path: <see cref="Generate(MediaInfo, VideoOutput)"/> uses hardcoded
/// tier bitrates and replicates the pre-AutoLadderConfig behaviour exactly.
///
/// New path: <see cref="GenerateLadder(MediaInfo, VideoCodecType, AutoLadderConfig)"/>
/// reads every parameter from <see cref="AutoLadderConfig"/> — no hardcoded constants.
/// </summary>
public class AbrLadderGenerator : IAbrLadderGenerator
{
    private readonly ILogger<AbrLadderGenerator> _logger;

    public AbrLadderGenerator(ILogger<AbrLadderGenerator>? logger = null)
    {
        _logger = logger ?? NullLogger<AbrLadderGenerator>.Instance;
    }

    // ── Legacy constants (kept only for the legacy Generate path) ──────────

    private static readonly LegacyTierDef[] LegacyTiers =
    [
        new(Height: 360, DefaultBitrateKbps: 800),
        new(Height: 480, DefaultBitrateKbps: 1400),
        new(Height: 720, DefaultBitrateKbps: 3000),
        new(Height: 1080, DefaultBitrateKbps: 6000),
        new(Height: 1440, DefaultBitrateKbps: 9000),
        new(Height: 2160, DefaultBitrateKbps: 15000),
    ];

    // ── IAbrLadderGenerator — legacy path ──────────────────────────────────

    public VideoOutput[] Generate(MediaInfo media, VideoOutput reference)
    {
        if (media.VideoStreams.Count == 0)
            return [];

        VideoStreamInfo source = media.VideoStreams[0];
        (int displayWidth, int displayHeight) = source.GetDisplayDimensions();
        source = source with { Width = displayWidth, Height = displayHeight };
        double aspect = (double)source.Width / source.Height;
        double complexityScale = ComputeComplexityScale(source);

        List<VideoOutput> ladder = [];
        foreach (LegacyTierDef tier in LegacyTiers)
        {
            if (tier.Height > source.Height)
                continue;

            int width = EvenRound((int)(tier.Height * aspect));
            int height = tier.Height;
            int bitrate = Math.Max(200, (int)Math.Round(tier.DefaultBitrateKbps * complexityScale));

            ladder.Add(reference with { Width = width, Height = height, BitrateKbps = bitrate });
        }

        if (ladder.Count > 0 && ladder[^1].Height < source.Height)
        {
            int bitrate = Math.Max(
                LegacyTiers[^1].DefaultBitrateKbps,
                (int)Math.Round(source.BitRateKbps * complexityScale)
            );
            ladder.Add(
                reference with
                {
                    Width = source.Width,
                    Height = source.Height,
                    BitrateKbps = bitrate,
                }
            );
        }

        return ladder.ToArray();
    }

    // ── New path — reads AutoLadderConfig ──────────────────────────────────

    /// <summary>
    /// Generates an ABR ladder from <paramref name="autoConfig"/>.
    /// Every parameter (tiers, bitrate strategy, codec policy, framerate reduction,
    /// gap collapse, MaxRungs cap) comes from the config — no hardcoded constants.
    /// </summary>
    public LadderRung[] GenerateLadder(
        MediaInfo media,
        VideoCodecType profileCodec,
        AutoLadderConfig autoConfig,
        VideoOutput? reference = null
    )
    {
        if (autoConfig.Tiers.Length == 0)
            throw new InvalidOperationException(
                "AutoLadderConfig.Tiers is empty. ProfileValidator should have rejected this profile before reaching the generator."
            );

        if (media.VideoStreams.Count == 0)
            return [];

        VideoStreamInfo source = media.VideoStreams[0];
        (int displayWidth, int displayHeight) = source.GetDisplayDimensions();
        source = source with { Width = displayWidth, Height = displayHeight };

        // Source-rung injection: when the source's width doesn't match any named
        // tier and falls below the ladder's top tier, inject a source-sized rung.
        // Default behaviour across every preset: never upscale, never drop the
        // highest-quality option, always emit a variant at the source's native
        // resolution when no named tier captures it. Bitrate scales from the next-
        // higher tier by pixel-count ratio so the HLS variant signals the right
        // bandwidth band; the next-higher tier (not the top tier) is the one whose
        // bandwidth bracket the source belongs in.
        //
        // 5% tolerance on the match check so a near-tier source (e.g. 1960x1080)
        // rides the named tier instead of creating a 1960x1080 rung next to the
        // 1080p rung — the named tier already covers it at its scale output.
        const double SourceTierMatchTolerance = 0.05;
        LadderTier[] effectiveTiers = autoConfig.Tiers;
        int maxTierWidth = effectiveTiers.Max(t => t.Width);
        bool sourceMatchesTier = effectiveTiers.Any(t =>
            Math.Abs(t.Width - source.Width) / (double)t.Width <= SourceTierMatchTolerance
        );

        if (!sourceMatchesTier && source.Width < maxTierWidth)
        {
            LadderTier referenceTier = effectiveTiers
                .Where(t => t.Width > source.Width)
                .OrderBy(t => t.Width)
                .First();

            long srcPx = (long)source.Width * source.Height;
            long refPx = (long)referenceTier.Width * referenceTier.Height;
            double ratio = refPx == 0 ? 1.0 : (double)srcPx / refPx;

            LadderTier sourceRung = new(
                Width: source.Width,
                Height: source.Height,
                Label: "source",
                RecommendedBitrateH264Kbps: referenceTier.RecommendedBitrateH264Kbps is int h264
                    ? (int)Math.Round(h264 * ratio)
                    : null,
                RecommendedBitrateHevcKbps: referenceTier.RecommendedBitrateHevcKbps is int hevc
                    ? (int)Math.Round(hevc * ratio)
                    : null,
                RecommendedBitrateAv1Kbps: referenceTier.RecommendedBitrateAv1Kbps is int av1
                    ? (int)Math.Round(av1 * ratio)
                    : null,
                RecommendedBitrateVp9Kbps: referenceTier.RecommendedBitrateVp9Kbps is int vp9
                    ? (int)Math.Round(vp9 * ratio)
                    : null
            );

            effectiveTiers = effectiveTiers.Append(sourceRung).ToArray();
        }

        // Step 1 — Filter candidate tiers.
        // NeverUpscale uses width-vs-width because the encoder pipeline scales via
        // `scale=W:-2`, so tier.Width is the actual output width. Height-only checks
        // wrongly filter 1080p (tier height 1080) on cinema sources (e.g. 2048x858
        // DCI 2K) where `scale=1920:-2` would yield 1920x803 — smaller than the
        // source in both dimensions, not an upscale.
        List<LadderTier> filtered = [];
        foreach (LadderTier tier in effectiveTiers)
        {
            if (autoConfig.NeverUpscale && tier.Width > source.Width)
                continue;

            // NeverUpsource: compute the tier bitrate first to compare with source.
            // When the source bitrate is unknown (0 — ffprobe sometimes only fills
            // container-level bitrate, not the video stream's) the comparison would
            // cut every tier; skip the filter instead of producing zero rungs.
            if (autoConfig.NeverUpsource && source.BitRateKbps > 0)
            {
                VideoCodecType tierCodec = SelectCodec(tier.Height, profileCodec, autoConfig);
                int candidateBitrate = ComputeBitrate(tier, source, tierCodec, autoConfig);
                if (candidateBitrate > source.BitRateKbps)
                    continue;
            }

            filtered.Add(tier);
        }

        // Step 2 — Build rungs.
        List<LadderRung> rungs = [];
        foreach (LadderTier tier in filtered)
        {
            VideoCodecType codec = SelectCodec(tier.Height, profileCodec, autoConfig);

            int bitrate = ComputeBitrate(tier, source, codec, autoConfig);
            int maxBitrate = (int)Math.Round(bitrate * autoConfig.VbrCeilingMultiplier);
            int bufSize = (int)Math.Round(bitrate * autoConfig.BufferSizeMultiplier);

            double framerate = source.FrameRate;
            if (
                autoConfig.ReduceFramerateForLowTiers
                && tier.Height <= autoConfig.LowTierFramerateThresholdHeight
            )
                framerate = source.FrameRate * autoConfig.LowTierFramerateMultiplier;

            // Reference settings (BitDepth / PixelFormat / CodecProfile) are tuned
            // for the profile's primary codec. When a Mixed-policy rung selects a
            // different codec, those settings must be overridden to safe per-codec
            // defaults so we don't emit invalid combos like H.264 + Main10 + 10-bit.
            (int bitDepth, string? pixelFormat, CodecProfile codecProfile) =
                codec == profileCodec
                    ? (
                        reference?.BitDepth ?? 8,
                        reference?.PixelFormat,
                        reference?.CodecProfile ?? CodecProfile.Auto
                    )
                    : CodecDefaults(codec);

            rungs.Add(
                new(
                    Width: tier.Width,
                    Height: ComputeAspectAwareHeight(tier.Width, source),
                    Codec: codec,
                    BitrateKbps: bitrate,
                    MaxBitrateKbps: maxBitrate,
                    BufferSizeKbps: bufSize,
                    Framerate: framerate,
                    Preset: reference?.Preset,
                    CodecProfile: codecProfile,
                    BitDepth: bitDepth,
                    PixelFormat: pixelFormat
                )
            );
        }

        // Step 2.5 — Emit explicit H.264 fallback rungs alongside the auto-selected
        // codec at the requested heights. Use case: the YouTube profile keeps the
        // 1080p tier on HEVC for efficiency but also ships a 1080p H.264 sibling
        // so HEVC-blocked clients (desktop Chrome without HEVC HW decode) still
        // get a 1080p variant in their ladder instead of dropping to 720p.
        // No-op when the height isn't in the filtered tier list or when an H.264
        // rung at that height already exists (codec policy already picked it).
        if (autoConfig.H264FallbackHeights.Length > 0)
        {
            foreach (int targetHeight in autoConfig.H264FallbackHeights)
            {
                LadderTier? tier = filtered.FirstOrDefault(t => t.Height == targetHeight);
                if (tier is null)
                    continue;

                bool alreadyH264 = rungs.Any(r =>
                    r.Height == targetHeight && r.Codec == VideoCodecType.H264
                );
                if (alreadyH264)
                    continue;

                int bitrate = ComputeBitrate(tier, source, VideoCodecType.H264, autoConfig);
                int maxBitrate = (int)Math.Round(bitrate * autoConfig.VbrCeilingMultiplier);
                int bufSize = (int)Math.Round(bitrate * autoConfig.BufferSizeMultiplier);

                double framerate = source.FrameRate;
                if (
                    autoConfig.ReduceFramerateForLowTiers
                    && tier.Height <= autoConfig.LowTierFramerateThresholdHeight
                )
                    framerate = source.FrameRate * autoConfig.LowTierFramerateMultiplier;

                (int bitDepth, string? pixelFormat, CodecProfile codecProfile) = CodecDefaults(
                    VideoCodecType.H264
                );

                rungs.Add(
                    new(
                        Width: tier.Width,
                        Height: ComputeAspectAwareHeight(tier.Width, source),
                        Codec: VideoCodecType.H264,
                        BitrateKbps: bitrate,
                        MaxBitrateKbps: maxBitrate,
                        BufferSizeKbps: bufSize,
                        Framerate: framerate,
                        Preset: reference?.Preset,
                        CodecProfile: codecProfile,
                        BitDepth: bitDepth,
                        PixelFormat: pixelFormat
                    )
                );
            }
        }

        // Step 3 — Collapse rungs whose bitrates are within MinTierGapPercent of an adjacent rung.
        rungs = CollapseClose(rungs, autoConfig.MinTierGapPercent);

        // Step 4 — Cap to MaxRungs (retain highest-resolution rungs).
        if (rungs.Count > autoConfig.MaxRungs)
        {
            _logger.LogInformation(
                "AutoLadder: capping {Before} rungs to MaxRungs={Max} (keeping top resolutions)",
                rungs.Count,
                autoConfig.MaxRungs
            );
            List<LadderRung> sorted = rungs.OrderByDescending(r => r.Height).ToList();
            rungs = sorted.Take(autoConfig.MaxRungs).ToList();
        }

        // Step 5 — Warn when below MinRungs; emit as-is (do not fabricate rungs).
        if (rungs.Count < autoConfig.MinRungs)
        {
            _logger.LogWarning(
                "AutoLadder produced {Actual} rung(s), which is below MinRungs={Min}. Emitting as-is — fabricating rungs to meet the floor is not supported.",
                rungs.Count,
                autoConfig.MinRungs
            );
        }

        return rungs.ToArray();
    }

    // ── Bitrate computation ────────────────────────────────────────────────

    private int ComputeBitrate(
        LadderTier tier,
        VideoStreamInfo source,
        VideoCodecType codec,
        AutoLadderConfig config
    )
    {
        switch (config.BitrateStrategy)
        {
            case BitrateStrategy.AppleHlsRecommended:
            case BitrateStrategy.BitrateLadder:
            {
                int? recommended = codec switch
                {
                    VideoCodecType.H265 => tier.RecommendedBitrateHevcKbps,
                    VideoCodecType.Av1 => tier.RecommendedBitrateAv1Kbps,
                    VideoCodecType.Vp9 => tier.RecommendedBitrateVp9Kbps,
                    _ => tier.RecommendedBitrateH264Kbps,
                };

                if (recommended.HasValue)
                    return recommended.Value;

                // Null recommendation → fall through to CrfBased with a warning.
                _logger.LogWarning(
                    "Tier '{Label}' has no recommended bitrate for codec {Codec} and strategy {Strategy}. Falling back to CrfBased.",
                    tier.Label,
                    codec,
                    config.BitrateStrategy
                );

                return ComputeCrfBasedBitrate(tier.Height, config.Crf);
            }

            case BitrateStrategy.PercentOfSource:
            {
                if (source.BitRateKbps <= 0 || source.Height <= 0)
                    return ComputeCrfBasedBitrate(tier.Height, config.Crf);

                double ratio = (double)tier.Height / source.Height;
                return (int)
                    Math.Round(
                        source.BitRateKbps * ratio * ratio * config.SourcePercentage / 100.0
                    );
            }

            case BitrateStrategy.CrfBased:
                return ComputeCrfBasedBitrate(tier.Height, config.Crf);

            default:
                return ComputeCrfBasedBitrate(tier.Height, config.Crf);
        }
    }

    /// <summary>
    /// CRF-based bitrate ceiling: uses a reference bitrate per megapixel scaled by
    /// the CRF value (lower CRF = higher quality = higher bitrate ceiling).
    /// Reference: ~6000 kbps at 1080p CRF 22 → ~3.125 kbps/pixel.
    /// Formula: referenceBitrateKbpsPerPixel × pixels × qualityFactor
    /// where qualityFactor = (51 - crf) / (51 - 22).
    /// </summary>
    private static int ComputeCrfBasedBitrate(int heightPixels, int crf)
    {
        int safeHeight = Math.Max(heightPixels, 1);
        double pixels = 16.0 / 9.0 * safeHeight * safeHeight;
        double qualityFactor = Math.Clamp((51.0 - crf) / (51.0 - 22.0), 0.1, 3.0);
        const double referenceKbpsPerPixel = 0.003;
        return Math.Max(100, (int)Math.Round(pixels * referenceKbpsPerPixel * qualityFactor));
    }

    // ── Codec selection ────────────────────────────────────────────────────

    /// <summary>
    /// Safe per-codec defaults for (BitDepth, PixelFormat, CodecProfile) when a
    /// Mixed-policy rung selects a codec different from the profile reference's.
    /// H.264 outputs target the High profile in 8-bit yuv420p (broadest browser
    /// + device coverage). H.265 / AV1 keep 10-bit Main10 since their tooling
    /// and the player kits we target both handle 10-bit transparently.
    /// </summary>
    private static (int BitDepth, string? PixelFormat, CodecProfile Profile) CodecDefaults(
        VideoCodecType codec
    ) =>
        codec switch
        {
            VideoCodecType.H264 => (8, "yuv420p", CodecProfile.High),
            VideoCodecType.H265 => (10, "yuv420p10le", CodecProfile.Main10),
            VideoCodecType.Av1 => (10, "yuv420p10le", CodecProfile.Main10),
            _ => (8, "yuv420p", CodecProfile.Auto),
        };

    private static VideoCodecType SelectCodec(
        int tierHeight,
        VideoCodecType profileCodec,
        AutoLadderConfig config
    )
    {
        if (config.CodecPolicy == LadderCodecPolicy.Uniform)
            return profileCodec;

        // Mixed: requires both LowTierCodec and HighTierCodec — validator rejects null upstream.
        if (config.LowTierCodec is null || config.HighTierCodec is null)
            throw new InvalidOperationException(
                "AutoLadderConfig.CodecPolicy=Mixed requires both LowTierCodec and HighTierCodec. "
                    + "ProfileValidator should have rejected this profile before reaching the generator."
            );

        return tierHeight <= config.MixedPolicySplitHeight
            ? config.LowTierCodec.Value
            : config.HighTierCodec.Value;
    }

    // ── Gap collapse ───────────────────────────────────────────────────────

    /// <summary>
    /// Removes rungs whose bitrate is within <paramref name="minGapPercent"/> of the
    /// next higher rung. Iterates from lowest to highest; when two adjacent rungs are
    /// "too close", the lower one is dropped (keep the higher-quality rung).
    /// </summary>
    private static List<LadderRung> CollapseClose(List<LadderRung> rungs, double minGapPercent)
    {
        if (minGapPercent <= 0 || rungs.Count <= 1)
            return rungs;

        // Sort ascending by height so we compare low→high pairs.
        List<LadderRung> sorted = rungs.OrderBy(r => r.Height).ThenBy(r => r.BitrateKbps).ToList();
        List<LadderRung> result = [sorted[^1]];

        for (int i = sorted.Count - 2; i >= 0; i--)
        {
            LadderRung candidate = sorted[i];
            LadderRung keepRef = result[^1];

            double upperBitrate = (double)keepRef.BitrateKbps;
            double lowerBitrate = (double)candidate.BitrateKbps;

            if (upperBitrate <= 0)
            {
                result.Add(candidate);
                continue;
            }

            double gapPercent = Math.Abs(upperBitrate - lowerBitrate) / upperBitrate * 100.0;
            if (gapPercent >= minGapPercent)
                result.Add(candidate);
        }

        // result is built high→low; reverse to restore ascending order.
        result.Reverse();
        return result;
    }

    // ── Legacy helpers ─────────────────────────────────────────────────────

    private static double ComputeComplexityScale(VideoStreamInfo source)
    {
        if (source.BitRateKbps <= 0 || source.Width <= 0 || source.Height <= 0)
            return 1.0;

        double megapixels = (source.Width * source.Height) / 1_000_000.0;
        if (megapixels < 0.1)
            return 1.0;

        double kbpsPerMp = source.BitRateKbps / megapixels;
        double scale = kbpsPerMp / 1000.0;
        return Math.Clamp(scale, 0.5, 1.2);
    }

    private static int EvenRound(int value) => value - (value % 2);

    /// <summary>
    /// Derive the output height for a ladder rung at <paramref name="outputWidth"/>
    /// from the source's aspect ratio. The encoder always emits <c>scale=W:-2</c>
    /// (width-fixed, height auto-rounded to a multiple of 2 from source AR), so
    /// the height we record on the rung must match what ffmpeg will actually
    /// produce — otherwise variant directory names, the master playlist's
    /// RESOLUTION attribute, and the BuildBranchFilter "needs scale?" check all
    /// disagree with the encoded file for non-16:9 sources (e.g. 2048x858 cinema
    /// AR would otherwise be tagged 1920x1080 while the file is actually 1920x804).
    /// </summary>
    private static int ComputeAspectAwareHeight(int outputWidth, VideoStreamInfo source)
    {
        if (source.Width <= 0 || source.Height <= 0)
            return 0;

        double computed = outputWidth * (double)source.Height / source.Width;
        int rounded = (int)Math.Round(computed);
        return rounded % 2 == 0 ? rounded : rounded + 1;
    }

    private record LegacyTierDef(int Height, int DefaultBitrateKbps);
}
