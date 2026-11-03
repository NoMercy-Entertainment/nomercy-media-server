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

namespace NoMercy.Encoder.LiveTranscode;

public class LiveQualitySelector(ICodecResolver codecResolver, IHardwareCapabilities hardware)
    : ILiveQualitySelector
{
    // Standard resolution tiers, from highest to lowest
    private static readonly (int Width, int Height)[] ResolutionTiers =
    [
        (3840, 2160),
        (1920, 1080),
        (1280, 720),
        (854, 480),
    ];

    public LiveQuality[] GetAvailableQualities(
        MediaInfo input,
        ClientCapabilities client,
        SpeedIndex speeds,
        IResourceBudget budget
    )
    {
        VideoStreamInfo? primaryVideo = input.VideoStreams.Count > 0 ? input.VideoStreams[0] : null;
        (int sourceWidth, int sourceHeight) = primaryVideo?.GetDisplayDimensions() ?? (1920, 1080);
        bool portrait = sourceWidth < sourceHeight;

        VideoCodecType targetCodec = ResolveTargetCodec(client);
        ResolvedCodec resolved = codecResolver.Resolve(targetCodec, hardware);

        string? deviceName = resolved.Device?.Name;
        bool isHardwareAccelerated = resolved.Device is not null;

        List<LiveQuality> qualities = [];

        foreach ((int tierWidth, int tierHeight) in ResolutionTiers)
        {
            (int outputWidth, int outputHeight) = portrait
                ? (tierHeight, tierWidth)
                : (tierWidth, tierHeight);
            // Skip resolutions larger than the source
            if (outputWidth > sourceWidth || outputHeight > sourceHeight)
                continue;

            qualities.Add(
                BuildQuality(
                    outputWidth,
                    outputHeight,
                    targetCodec,
                    resolved,
                    speeds,
                    deviceName,
                    isHardwareAccelerated
                )
            );
        }

        // Sources smaller than every tier (e.g. sub-480p) would otherwise
        // leave the candidate set empty — keep the smallest tier so callers
        // never select from an empty sequence.
        if (qualities.Count == 0)
        {
            (int fallbackWidth, int fallbackHeight) = ResolutionTiers[^1];
            if (portrait)
                (fallbackWidth, fallbackHeight) = (fallbackHeight, fallbackWidth);
            qualities.Add(
                BuildQuality(
                    fallbackWidth,
                    fallbackHeight,
                    targetCodec,
                    resolved,
                    speeds,
                    deviceName,
                    isHardwareAccelerated
                )
            );
        }

        return [.. qualities];
    }

    private static LiveQuality BuildQuality(
        int tierWidth,
        int tierHeight,
        VideoCodecType targetCodec,
        ResolvedCodec resolved,
        SpeedIndex speeds,
        string? deviceName,
        bool isHardwareAccelerated
    )
    {
        int bitrateKbps = EstimateBitrateKbps(tierWidth, tierHeight);

        double speedMultiplier = speeds.GetSpeedMultiplier(
            targetCodec,
            resolved.FfmpegEncoderName,
            Math.Max(tierWidth, tierHeight),
            deviceName
        );

        bool canRealtime = speedMultiplier >= 1.2;

        string qualityId = $"{Math.Min(tierWidth, tierHeight)}p";
        string label = qualityId;

        return new(
            Id: qualityId,
            Label: label,
            Width: tierWidth,
            Height: tierHeight,
            Codec: targetCodec,
            BitrateKbps: bitrateKbps,
            Encoder: resolved.FfmpegEncoderName,
            IsHardwareAccelerated: isHardwareAccelerated,
            ExpectedSpeed: speedMultiplier,
            CanRealtime: canRealtime
        );
    }

    public LiveQuality SelectOptimal(
        MediaInfo input,
        ClientCapabilities client,
        SpeedIndex speeds,
        IResourceBudget budget
    )
    {
        LiveQuality[] candidates = GetAvailableQualities(input, client, speeds, budget);

        VideoCodecType[]? allowedCodecs = ResolveAllowedCodecs(client);

        // Filter by client capabilities
        IEnumerable<LiveQuality> allowed = candidates.Where(q =>
            FitsClientDimensions(q, client)
            && (
                allowedCodecs is null
                || allowedCodecs.Length == 0
                || allowedCodecs.Contains(q.Codec)
            )
        );

        // Pick highest CanRealtime quality
        LiveQuality? optimal = allowed
            .Where(q => q.CanRealtime)
            .OrderByDescending(q => q.Width)
            .ThenByDescending(q => q.Height)
            .FirstOrDefault();

        // No CanRealtime candidates → fall back to lowest resolution
        if (optimal is null)
        {
            optimal = allowed.OrderBy(q => q.Width).ThenBy(q => q.Height).FirstOrDefault();
        }

        // Absolute fallback: use the single lowest tier from all candidates
        optimal ??= candidates.OrderBy(q => q.Width).ThenBy(q => q.Height).First();

        return optimal;
    }

    private static bool FitsClientDimensions(LiveQuality quality, ClientCapabilities client)
    {
        if (client.MaxWidth is int maxWidth && client.MaxHeight is int maxHeight)
            return Math.Max(quality.Width, quality.Height) <= Math.Max(maxWidth, maxHeight)
                && Math.Min(quality.Width, quality.Height) <= Math.Min(maxWidth, maxHeight);

        return quality.Width <= (client.MaxWidth ?? int.MaxValue)
            && quality.Height <= (client.MaxHeight ?? int.MaxValue);
    }

    public LiveQuality SelectForBandwidth(
        LiveQuality[] available,
        int observedBandwidthKbps,
        double usableFraction,
        LiveQuality current
    )
    {
        if (available.Length == 0)
            return current;

        double budgetKbps = observedBandwidthKbps * usableFraction;

        // `available` is ordered highest-to-lowest resolution (GetAvailableQualities
        // walks ResolutionTiers top-down), so the first tier whose bitrate fits the
        // budget is the highest one the downlink can sustain.
        foreach (LiveQuality quality in available)
        {
            if (quality.BitrateKbps <= budgetKbps)
                return quality;
        }

        // Nothing fits — a heavily constrained downlink. The lowest tier is still
        // the best available option; never leave the caller without a selection.
        return available[^1];
    }

    // Choose the transcode target codec by honouring the CLIENT's own
    // preference order. A client lists codecs best-first: browsers put H264
    // first because it is the most reliably MSE-decodable, then HEVC/AV1 as
    // bandwidth-saving fallbacks. Imposing a server-side "H265 first" order
    // here defeated the point of transcoding for a browser — it handed an HEVC
    // stream to a client that only listed HEVC as a last resort. Pick the first
    // codec the client lists that we can actually encode.
    private static readonly VideoCodecType[] EncodableCodecs =
    [
        VideoCodecType.H264,
        VideoCodecType.H265,
        VideoCodecType.Av1,
        VideoCodecType.Vp9,
    ];

    private static VideoCodecType ResolveTargetCodec(ClientCapabilities client)
    {
        foreach (VideoCodecType codec in ResolveAllowedCodecs(client) ?? [])
        {
            if (EncodableCodecs.Contains(codec))
                return codec;
        }

        // Empty list or nothing we can encode — H264 is the universal baseline.
        return VideoCodecType.H264;
    }

    /// <summary>
    /// New-shape clients declare per-codec capability in <see cref="ClientCapabilities.Video"/>;
    /// older builds still send the flat <see cref="ClientCapabilities.SupportedVideoCodecs"/>
    /// list. Mirrors PlaybackDecisionEngine's legacy-payload synthesis so a new-shape-only
    /// client is filtered by its declared codecs instead of silently allowing every codec.
    /// </summary>
    private static VideoCodecType[]? ResolveAllowedCodecs(ClientCapabilities client) =>
        client.Video.Length > 0
            ? [.. client.Video.Select(v => v.Codec)]
            : client.SupportedVideoCodecs;

    private static int EstimateBitrateKbps(int width, int height) =>
        (Math.Max(width, height), Math.Min(width, height)) switch
        {
            (3840, 2160) => 20000,
            (1920, 1080) => 8000,
            (1280, 720) => 4000,
            _ => 2000,
        };
}
