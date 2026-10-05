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
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.BuildingBlocks;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.ContentAnalysis;
using NoMercy.Encoder.Errors;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.Hdr;
using NoMercy.Encoder.Naming;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.Pipeline.Optimizer;
using NoMercy.Encoder.Profiles;
using NoMercy.Encoder.Subtitles;

namespace NoMercy.Encoder.Pipeline.Stages;

public record ExecutionPlan(
    ExecutionGroup[] Groups,
    TimeSpan EstimatedTotalDuration,
    OutputPlan OutputPlan
);

public class PlanStage(
    ExecutionGraphBuilder graphBuilder,
    GroupingStrategy groupingStrategy,
    CostEstimator costEstimator,
    ICodecResolver codecResolver,
    IHardwareCapabilities hardware,
    ITonemapSelector tonemapSelector,
    IFfmpegCapabilities ffmpegCapabilities,
    IAbrLadderGenerator abrLadderGenerator,
    ICropDetector cropDetector,
    ILogger<PlanStage> logger,
    IQualityScalerResolver? qualityScalerResolver = null,
    IHardwarePreferenceResolver? hardwarePreferenceResolver = null,
    SpeedIndex? speedIndex = null,
    IBitDepthPolicyResolver? bitDepthPolicyResolver = null,
    IOutputNamingResolver? outputNamingResolver = null,
    ISubtitleAcquisitionService? subtitleAcquisitionService = null,
    Composition.EncoderOptions? options = null,
    IEncodeYieldProbe? encodeYieldProbe = null
) : IPipelineStage<ValidateInput, ExecutionPlan>, IPlanStage
{
    public string Name => "Plan";

    // Minimum letterbox / pillarbox thickness (per axis, in source pixels)
    // before an auto-detected crop is honoured. Below this a detected border is
    // treated as noise and ignored so copyable rungs are not forced into a
    // full transcode; at or above it the bars are real and removed even though
    // cropping disables stream-copy on every rung it touches.
    private const int CropBarThresholdPx = 100;

    // Stateless — the resolver holds no configuration, so PlanStage owns one
    // instance rather than threading it through DI like the interface-backed
    // dependencies above (matches how PlanStageHelpers / PlanStageDisambiguation
    // are consumed as plain collaborators, not injected services).
    private readonly StreamActionResolver _streamActionResolver = new();

    public async Task<StageResult> ExecuteAsync(
        ValidateInput input,
        EncodingContext context,
        CancellationToken ct
    )
    {
        logger.LogInformation("[{CorrelationId}] Planning execution", context.CorrelationId);

        try
        {
            EncodingProfile profile = AutoLadderExpander.Expand(
                abrLadderGenerator,
                logger,
                input.Profile,
                input.Media
            );
            string? cropFilter = await ResolveCropFilterAsync(
                profile,
                input.Media,
                context.CorrelationId,
                ct
            );

            // Resolve codecs honouring profile.HardwarePreference.
            // When HardwarePreferenceResolver is available, it is the single source of
            // truth for encoder selection and emits a decision log entry per output.
            // The GPU session cap (maxHwSessions) is still enforced: outputs that would
            // exceed it are demoted to ForceSoftware regardless of the profile setting.
            int maxHwSessions = hardware.HasGpu ? hardware.Gpus.Min(g => g.MaxEncoderSessions) : 0;
            int hwSessionsUsed = 0;

            VideoOutput[] videoOutputs = PlanStageHelpers.EnumerateVideo(profile);

            // EmitHdrAndSdr (HDR source): split coverage along the bit-depth /
            // codec role. 10-bit rungs (HEVC Main10) preserve HDR via
            // passthrough; 8-bit rungs (H.264) carry the tonemapped SDR copy.
            // Each 10-bit rung emits ONE output (HDR), each 8-bit rung emits
            // ONE output (SDR) — no per-rung doubling. Net coverage for an
            // HDR source therefore relies on the ladder containing both 10-bit
            // and 8-bit rungs at the heights you want available in both ranges
            // (e.g. YouTube's H264FallbackHeights = [1080]). SDR-only clients
            // at heights without an 8-bit rung simply step down to the next
            // height that has one.
            //
            // Rationale: an SDR-tonemapped HEVC copy at a height that already
            // has an AVC SDR fallback is redundant — the AVC sibling already
            // covers HEVC-blocked / SDR-only clients. Doubling the 10-bit
            // rung wastes encode time + storage for a niche middle case
            // (HEVC-capable but cannot tonemap HDR client-side) that almost
            // no real-world client falls into.
            //
            // SDR source: this block is skipped (IsHdr gate); rungs emit
            // SDR as-is from EnumerateVideo regardless of bit depth.
            if (
                profile.HdrPolicies == HdrPolicies.EmitHdrAndSdr
                && input.Media.VideoStreams.Count > 0
                && input.Media.VideoStreams[0].IsHdr
            )
            {
                videoOutputs = videoOutputs
                    .Select(v =>
                        v.BitDepth >= 10
                            ? v with
                            {
                                ConvertHdrToSdr = false,
                            }
                            : v with
                            {
                                ConvertHdrToSdr = true,
                            }
                    )
                    .ToArray();
            }

            // Smart-copy downgrade — must mirror the identical call in
            // BuildOutputPlanAsync (see the lockstep comment there) so
            // resolvedCodecs and VideoOutputPlan stay aligned by index. Decisions
            // are suppressed here (NullDecisionLogSink) so the copy-vs-transcode
            // choice is logged exactly once, from BuildOutputPlanAsync.
            // Measured once, then handed to both downgrade calls so they cannot
            // reach different conclusions about the same file.
            long? probedYieldKbps = await ProbeSmartCopyYieldAsync(
                    videoOutputs,
                    input.Media,
                    profile,
                    cropFilter,
                    ct
                )
                .ConfigureAwait(false);

            videoOutputs = ApplySmartCopyDowngrade(
                videoOutputs,
                input.Media,
                profile,
                cropFilter,
                NullDecisionLogSink.Instance,
                probedYieldKbps
            );

            ResolvedCodec[] resolvedCodecs;

            if (hardwarePreferenceResolver is not null)
            {
                SpeedIndex effectiveSpeedIndex = speedIndex ?? new SpeedIndex(new());

                List<string> availableEncoderNames = ffmpegCapabilities
                    .AvailableEncoders.Where(encoderName =>
                        IsHardwareEncoderSelectable(encoderName, hardware)
                    )
                    .ToList();

                List<ResolvedCodec> codecList = [];

                foreach (VideoOutput v in videoOutputs)
                {
                    // Clamp to ForceSoftware when the GPU session cap is exhausted.
                    HardwarePreference effectivePreference =
                        hwSessionsUsed >= maxHwSessions && maxHwSessions > 0
                            ? HardwarePreference.ForceSoftware
                            : profile.HardwarePreference;

                    HardwareResolutionResult resolution = hardwarePreferenceResolver.Resolve(
                        v.Codec,
                        effectivePreference,
                        availableEncoderNames,
                        effectiveSpeedIndex,
                        context.DecisionsOrNoOp
                    );

                    if (resolution.Failure is not null)
                    {
                        return new StageFailure(
                            new(
                                EncodingErrorKind.HardwareUnavailable,
                                resolution.Failure.Shape.Message,
                                null,
                                Name,
                                false
                            )
                        );
                    }

                    // The HW preference resolver already picked an encoder by name from
                    // the SpeedIndex (which is the authoritative source of "this works
                    // on this host"). Look up the EncoderInfo directly instead of
                    // re-resolving via preference — the legacy CodecResolver path gates
                    // on IHardwareCapabilities.HasGpu, which silently downgrades to
                    // software when GPU detection is stale even though SpeedIndex has
                    // hevc_nvenc benchmarks for it.
                    ResolvedCodec resolved = codecResolver.ResolveByEncoderName(
                        v.Codec,
                        resolution.EncoderHandle!,
                        hardware
                    );

                    if (resolved.Device is not null)
                        hwSessionsUsed++;

                    codecList.Add(resolved);
                }

                resolvedCodecs = codecList.ToArray();
            }
            else
            {
                // Legacy path — no resolver injected (e.g. older test suites).
                resolvedCodecs = videoOutputs
                    .Select(v =>
                    {
                        EncoderPreference preference =
                            hwSessionsUsed < maxHwSessions
                                ? EncoderPreference.PreferHardware
                                : EncoderPreference.ForceSoftware;

                        ResolvedCodec resolved = codecResolver.Resolve(
                            v.Codec,
                            hardware,
                            preference
                        );

                        if (resolved.Device is not null)
                            hwSessionsUsed++;

                        return resolved;
                    })
                    .ToArray();
            }

            List<ExecutionNode> nodes = graphBuilder.BuildGraph(
                input.Media,
                profile,
                resolvedCodecs
            );

            List<ExecutionGroup> groups = groupingStrategy.GroupNodes(nodes, hardware);

            TimeSpan totalEstimate = costEstimator.EstimateTotal(groups, input.Media.Duration);

            OutputPlan outputPlan = await BuildOutputPlanAsync(
                    profile,
                    input.Media,
                    resolvedCodecs,
                    cropFilter,
                    context,
                    probedYieldKbps,
                    ct
                )
                .ConfigureAwait(false);

            logger.LogInformation(
                "[{CorrelationId}] Plan: {Groups} groups, estimated {Duration}",
                context.CorrelationId,
                groups.Count,
                totalEstimate
            );

            IReadOnlyList<AcquiredSubtitle> acquiredSubtitles = await AcquireSubtitlesAsync(
                    profile,
                    input.Media,
                    ct
                )
                .ConfigureAwait(false);

            outputPlan = outputPlan with { AcquiredSubtitles = acquiredSubtitles };

            GpuAccelPlan? gpuAccel = ResolveGpuAccel(outputPlan);
            if (gpuAccel is not null)
                outputPlan = outputPlan with { GpuAccel = gpuAccel };

            ExecutionPlan plan = new(groups.ToArray(), totalEstimate, outputPlan);
            return new StageSuccess<ExecutionPlan>(plan);
        }
        catch (EncoderRuntimeException rte)
        {
            return new StageFailure(
                new(EncodingErrorKind.HardwareUnavailable, rte.Shape.Message, null, Name, false)
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new StageFailure(
                new(EncodingErrorKind.Unknown, $"Planning failed: {ex.Message}", null, Name, false)
            );
        }
    }

    /// <summary>
    /// Gate on the real hardware-encoder init probe result before an
    /// ffmpeg-advertised hardware encoder name is treated as selectable.
    /// <see cref="IFfmpegCapabilities.AvailableEncoders"/> only reflects which
    /// encoders the running ffmpeg binary was compiled with — the NoMercy fork
    /// advertises every hardware encoder it knows how to build regardless of
    /// what GPU (if any) is actually installed. GPU-vendor detection alone is
    /// not sufficient evidence either — a host can have the right vendor's
    /// card yet lack the specific encoder block (older GPU generation, driver
    /// too old, no supporting silicon) and still see the name in the
    /// compiled list.
    ///
    /// <see cref="IHardwareCapabilities.UsableHardwareEncoders"/> is populated
    /// once at boot by <see cref="HardwareEncoderProbe"/>, which runs a real
    /// one-frame init through ffmpeg for every candidate. Only a name that
    /// survives that probe may be selected here — this supersedes the earlier
    /// vendor-presence gate as the selection authority (Fillz's 7 stuck
    /// h264_amf child jobs on an NVIDIA-only host: the vendor gate alone still
    /// let hevc_amf through on hardware that merely happened to be AMD-vendor
    /// with a driver too old to actually open the codec). Vendor detection
    /// remains the signal for GPU session-budget sizing (<c>maxHwSessions</c>
    /// above), just not for whether a name is a legal selection.
    /// </summary>
    private static bool IsHardwareEncoderSelectable(
        string ffmpegEncoderName,
        IHardwareCapabilities hardware
    )
    {
        GpuVendor? requiredVendor = GpuEncoderTokens.VendorForEncoderName(ffmpegEncoderName);
        if (requiredVendor is null)
            return true; // software encoder, or a name outside the known vendor lists

        return hardware.UsableHardwareEncoders.Contains(ffmpegEncoderName);
    }

    /// <summary>
    /// Downgrades an individual <see cref="VideoOutput"/> from Transcode to
    /// Copy when the source stream already satisfies the profile losslessly —
    /// re-encoding a matching lossy source can only add generation loss, never
    /// shrink it. Must be called identically from both places
    /// <see cref="PlanStageHelpers.EnumerateVideo"/> is expanded (see the
    /// EmitHdrAndSdr lockstep comments at both call sites) so the resulting
    /// array stays aligned by index with <c>resolvedCodecs</c> / <c>videoPlan</c>.
    ///
    /// Per-rung: a ladder copies only the rung whose resolution matches the
    /// source and transcodes the rest — that is the intended outcome, not a
    /// bug to guard against.
    ///
    /// All of the following must hold, or the output is left as Transcode:
    /// <list type="number">
    ///   <item><see cref="StreamActionResolver.ResolveVideo"/> resolves Copy
    ///     (codec, resolution, bitrate, and bit-depth all match).</item>
    ///   <item>The output container can actually carry that codec —
    ///     <see cref="ContainerCompatibility.SupportsVideo"/>. This is the one
    ///     check <see cref="ProfileValidator"/> skips for Copy-policy streams,
    ///     so it must be enforced here (e.g. an HEVC source can never copy into
    ///     <see cref="Container.HlsTs"/>, which only carries H.264).</item>
    ///   <item>No tonemap in play (<c>ConvertHdrToSdr</c> resolved true for an
    ///     HDR source) — copy cannot also route through a filter.</item>
    ///   <item>No crop filter in play — stream copy cannot route through
    ///     filter_complex either (see the MapLabel comment in
    ///     BuildOutputPlanAsync).</item>
    ///   <item><see cref="HdrPolicies.AlwaysTonemap"/> forces a strip even when
    ///     the reference <see cref="VideoOutput.ConvertHdrToSdr"/> was left
    ///     false by the profile author — checked independently of
    ///     ConvertHdrToSdr for exactly that gap.</item>
    /// </list>
    ///
    /// On downgrade, <c>Codec</c> is switched to <see cref="VideoCodecType.Copy"/>
    /// alongside <c>Policy</c> — this is what makes
    /// <see cref="ICodecResolver"/> / <see cref="IHardwarePreferenceResolver"/>
    /// resolve the literal "copy" pseudo-encoder (the same path the
    /// author-declared always-copy preset already uses), so <c>EncoderName</c>
    /// downstream is "copy" and not a real encoder asked to re-encode a direct
    /// stream map.
    /// </summary>
    /// <summary>
    /// The share of the source bitrate a re-encode has to come in under before it
    /// is judged worth doing. A measured yield close to the source buys nothing
    /// once generation loss and GPU hours are counted, and the probe samples only
    /// part of the file, so the gain has to be clear rather than marginal.
    /// </summary>
    private const double WorthReencodingBelowSourceShare = 0.7;

    /// <summary>
    /// Measures what the profile's settings would actually yield, but only when
    /// the answer can change the outcome: the source must otherwise be about to
    /// be stream-copied, and the target must be quality-targeted (CRF), since a
    /// bitrate-targeted output already states its own size.
    /// </summary>
    private async Task<long?> ProbeSmartCopyYieldAsync(
        VideoOutput[] videoOutputs,
        MediaInfo media,
        EncodingProfile profile,
        string? cropFilter,
        CancellationToken ct
    )
    {
        if (encodeYieldProbe is null || media.VideoStreams.Count == 0 || cropFilter is not null)
            return null;

        if (profile.HdrPolicies == HdrPolicies.AlwaysTonemap)
            return null;

        VideoStreamInfo source = media.VideoStreams[0];
        if (source.BitRateKbps <= 0)
            return null;

        VideoOutput? candidate = videoOutputs.FirstOrDefault(v =>
            v.Policy == StreamPolicy.Transcode
            && v.RateControl == Profiles.RateControlMode.Crf
            && !(v.ConvertHdrToSdr && source.IsHdr)
            && _streamActionResolver.ResolveVideo(source, v) == StreamAction.Copy
            && ContainerCompatibility.SupportsVideo(profile.Container, v.Codec)
        );

        if (candidate is null)
            return null;

        // Measure with the encoder that will actually run. A hardware encoder
        // asked for the same quality number spends materially more bitrate than
        // the software one, so measuring x265 and then encoding with NVENC would
        // understate the output — far enough that a source could be judged worth
        // re-encoding and then come out larger than it started.
        string? encoderName = ResolvePlannedEncoderName(candidate.Codec, profile);

        return await encodeYieldProbe
            .EstimateBitrateKbpsAsync(
                media.FilePath,
                new(
                    candidate.Codec,
                    candidate.Crf,
                    candidate.Preset,
                    candidate.Tune,
                    candidate.PixelFormat,
                    encoderName
                ),
                media.Duration,
                ct
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The ffmpeg encoder this plan will select for a codec, resolved the same
    /// way the codec pass resolves it. Null when it cannot be determined, which
    /// leaves the probe on the software encoder for that codec.
    /// </summary>
    private string? ResolvePlannedEncoderName(VideoCodecType codec, EncodingProfile profile)
    {
        if (hardwarePreferenceResolver is null)
            return null;

        List<string> availableEncoderNames = ffmpegCapabilities
            .AvailableEncoders.Where(encoderName =>
                IsHardwareEncoderSelectable(encoderName, hardware)
            )
            .ToList();

        HardwareResolutionResult resolution = hardwarePreferenceResolver.Resolve(
            codec,
            profile.HardwarePreference,
            availableEncoderNames,
            speedIndex ?? new SpeedIndex(new()),
            NullDecisionLogSink.Instance
        );

        return resolution.Failure is null ? resolution.EncoderHandle : null;
    }

    private VideoOutput[] ApplySmartCopyDowngrade(
        VideoOutput[] videoOutputs,
        MediaInfo media,
        EncodingProfile profile,
        string? cropFilter,
        IDecisionLogSink decisions,
        long? probedYieldKbps
    )
    {
        if (media.VideoStreams.Count == 0 || cropFilter is not null)
            return videoOutputs;

        if (profile.HdrPolicies == HdrPolicies.AlwaysTonemap)
            return videoOutputs;

        VideoStreamInfo source = media.VideoStreams[0];

        return videoOutputs
            .Select(v => TryDowngradeToSmartCopy(v, source, profile, decisions, probedYieldKbps))
            .ToArray();
    }

    private VideoOutput TryDowngradeToSmartCopy(
        VideoOutput v,
        VideoStreamInfo source,
        EncodingProfile profile,
        IDecisionLogSink decisions,
        long? probedYieldKbps
    )
    {
        if (v.Policy != StreamPolicy.Transcode)
            return v;

        if (v.ConvertHdrToSdr && source.IsHdr)
            return v;

        if (_streamActionResolver.ResolveVideo(source, v) != StreamAction.Copy)
            return v;

        if (!ContainerCompatibility.SupportsVideo(profile.Container, v.Codec))
            return v;

        // Matching the target on codec, resolution and bit depth says the source
        // is the right shape, not that it is the right size — a source can carry
        // the same picture at several times the bitrate the profile would spend.
        // Where that was measured, re-encoding is the whole point, so let it.
        if (
            probedYieldKbps is > 0
            && source.BitRateKbps > 0
            && probedYieldKbps < source.BitRateKbps * WorthReencodingBelowSourceShare
        )
        {
            decisions.Add(
                new(
                    "plan",
                    "plan.video_reencode_shrinks",
                    $"Source {source.Codec} matches the target but carries "
                        + $"{source.BitRateKbps} kbps where CRF {v.Crf} measured ~{probedYieldKbps} kbps "
                        + "on this content — re-encoding instead of copying.",
                    new
                    {
                        sourceKbps = source.BitRateKbps,
                        probedKbps = probedYieldKbps,
                        crf = v.Crf,
                    }
                )
            );

            return v;
        }

        decisions.Add(
            new(
                "plan",
                "plan.video_smart_copy",
                $"Source {source.Codec} {source.Width}x{source.Height} ({source.BitDepth}-bit) "
                    + "already matches the profile target — stream-copying instead of re-encoding.",
                new
                {
                    codec = source.Codec,
                    width = source.Width,
                    height = source.Height,
                    bitDepth = source.BitDepth,
                }
            )
        );

        return v with
        {
            Policy = StreamPolicy.Copy,
            Codec = VideoCodecType.Copy,
        };
    }

    /// <summary>
    /// Downgrades an individual <see cref="AudioOutput"/> from Transcode to
    /// Copy when every source stream it would match already satisfies it
    /// losslessly — the audio equivalent of
    /// <see cref="ApplySmartCopyDowngrade"/>. Re-encoding a matching lossy
    /// source can only add generation loss and CPU time, never shrink it.
    ///
    /// Evaluated per profile-level <see cref="AudioOutput"/> (not per source
    /// stream), matching the granularity the profile author reasons in — a
    /// preset asking AAC+E-AC-3 from an AAC source copies the AAC output and
    /// still transcodes toward E-AC-3. Because one AudioOutput can match
    /// several source streams (multiple languages), the downgrade only
    /// applies when EVERY matched stream individually resolves to Copy —
    /// otherwise a heterogeneous-codec source (e.g. an AAC English track next
    /// to a DTS Japanese one, both matched by the same AllowedLanguages rule)
    /// would end up mapping "-c:a copy" onto a stream it cannot actually
    /// carry unmodified.
    /// </summary>
    private AudioOutput[] ApplySmartCopyDowngradeAudio(
        AudioOutput[] audioOutputs,
        MediaInfo media,
        EncodingProfile profile,
        IDecisionLogSink decisions
    )
    {
        if (media.AudioStreams.Count == 0)
            return audioOutputs;

        OutputFormat outputFormat = PlanStageHelpers.ContainerToOutputFormat(profile.Container);

        return audioOutputs
            .Select(a =>
                TryDowngradeAudioToSmartCopy(
                    a,
                    media.AudioStreams,
                    profile,
                    outputFormat,
                    decisions
                )
            )
            .ToArray();
    }

    private AudioOutput TryDowngradeAudioToSmartCopy(
        AudioOutput audioOutput,
        IReadOnlyList<AudioStreamInfo> sourceStreams,
        EncodingProfile profile,
        OutputFormat outputFormat,
        IDecisionLogSink decisions
    )
    {
        if (audioOutput.Policy != StreamPolicy.Transcode)
            return audioOutput;

        HashSet<string> allowed =
            audioOutput.AllowedLanguages.Length > 0
                ? new HashSet<string>(
                    audioOutput.AllowedLanguages,
                    StringComparer.OrdinalIgnoreCase
                )
                : [];

        List<AudioStreamInfo> matched = sourceStreams
            .Where(s => allowed.Count == 0 || allowed.Contains(s.Language ?? "und"))
            .ToList();

        if (matched.Count == 0)
            return audioOutput;

        bool allCopy = matched.All(s =>
            _streamActionResolver.ResolveAudio(s, audioOutput, outputFormat) == StreamAction.Copy
        );

        if (!allCopy)
            return audioOutput;

        if (!ContainerCompatibility.SupportsAudio(profile.Container, audioOutput.Codec))
            return audioOutput;

        decisions.Add(
            new(
                "plan",
                "plan.audio_smart_copy",
                $"Source audio ({matched.Count} stream(s)) already matches the profile "
                    + $"target {audioOutput.Codec} — stream-copying instead of re-encoding.",
                new { codec = audioOutput.Codec, streams = matched.Count }
            )
        );

        return audioOutput with
        {
            Policy = StreamPolicy.Copy,
            Codec = AudioCodecType.Copy,
        };
    }

    /// <summary>
    /// Resolves the GPU-resident decode+scale plan for this output, or null for
    /// the CPU path. Dark by default: only when <c>EncoderOptions.EnableGpuResident</c>
    /// is opted in, the host has a GPU, the plan is eligible (no tonemap / crop /
    /// burn-in), has no thumbnails (sprites need a CPU download), and the vendor's
    /// GPU scaler is present in the running ffmpeg build.
    /// </summary>
    private GpuAccelPlan? ResolveGpuAccel(OutputPlan outputPlan)
    {
        bool hasGpu = hardware is { HasGpu: true, Gpus.Count: > 0 };
        return GpuResidentActivation.Resolve(
            options?.EnableGpuResident == true,
            hasGpu,
            hasGpu ? hardware.Gpus[0].Vendor : null,
            outputPlan,
            ffmpegCapabilities.HasFilter
        );
    }

    /// <summary>
    /// When the profile opts into <see cref="EncodingProfile.AutoDetectCrop"/>
    /// and the source has a video stream, run the crop detector and return
    /// a <c>W:H:X:Y</c> string suitable for the <c>crop=</c> filter. Returns
    /// null when the profile disables auto-crop, the source has no video,
    /// or detection concludes the frame is already letterbox-free.
    /// </summary>
    private async Task<string?> ResolveCropFilterAsync(
        EncodingProfile profile,
        MediaInfo media,
        string correlationId,
        CancellationToken ct
    )
    {
        if (!profile.AutoDetectCrop || media.VideoStreams.Count == 0)
            return null;

        try
        {
            VideoStreamInfo sourceStream = media.VideoStreams[0];

            CropResult crop = await cropDetector
                .DetectAsync(
                    media.FilePath,
                    sourceVideoFileId: null,
                    sourceIsHdr: sourceStream.IsHdr,
                    ct
                )
                .ConfigureAwait(false);

            if (!crop.ShouldCrop)
                return null;

            // Only crop when the bars are big enough to matter. A crop forces a
            // re-encode of every rung it touches (you cannot crop a stream-copy),
            // so a few stray pixels of detected border are not worth turning an
            // otherwise copyable 4K rung into a full transcode. Bars over the
            // threshold on either axis are real letterbox / pillarbox and MUST
            // be removed even at the cost of the copy.
            int barsHorizontal = sourceStream.Width - crop.Width;
            int barsVertical = sourceStream.Height - crop.Height;

            if (barsHorizontal <= CropBarThresholdPx && barsVertical <= CropBarThresholdPx)
            {
                logger.LogInformation(
                    "[{CorrelationId}] Crop detected ({W}x{H}+{X}+{Y}) but bars "
                        + "({BarsH}px h / {BarsV}px v) are within the {Threshold}px tolerance "
                        + "— leaving frame uncropped so copyable rungs stay copyable.",
                    correlationId,
                    crop.Width,
                    crop.Height,
                    crop.X,
                    crop.Y,
                    barsHorizontal,
                    barsVertical,
                    CropBarThresholdPx
                );
                return null;
            }

            logger.LogInformation(
                "[{CorrelationId}] Crop forced: bars {BarsH}px h / {BarsV}px v exceed "
                    + "{Threshold}px → {W}x{H}+{X}+{Y}. Re-encode required (stream-copy cannot crop).",
                correlationId,
                barsHorizontal,
                barsVertical,
                CropBarThresholdPx,
                crop.Width,
                crop.Height,
                crop.X,
                crop.Y
            );

            return $"{crop.Width}:{crop.Height}:{crop.X}:{crop.Y}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort: a crop detection failure shouldn't fail the encode.
            // The log surfaces the issue so users can disable AutoDetectCrop if
            // the source consistently trips the detector.
            logger.LogWarning(
                ex,
                "[{CorrelationId}] Crop detection failed — continuing without crop",
                correlationId
            );
            return null;
        }
    }

    private async Task<IReadOnlyList<AcquiredSubtitle>> AcquireSubtitlesAsync(
        EncodingProfile profile,
        MediaInfo media,
        CancellationToken ct
    )
    {
        SubtitleAcquisitionConfig? acq = profile.SubtitleAcquisition;
        if (subtitleAcquisitionService is null || acq is null || !acq.Enabled)
            return [];

        try
        {
            string[] alreadyPresent = media
                .SubtitleStreams.Select(s => s.Language ?? "und")
                .ToArray();

            AcquisitionRequest request = new(
                SourcePath: media.FilePath,
                SourceFileSize: media.FileSizeBytes,
                SourceFilename: Path.GetFileName(media.FilePath),
                MediaTitle: Path.GetFileNameWithoutExtension(media.FilePath),
                Season: null,
                Episode: null,
                Year: null,
                SourceFps: media.VideoStreams.Count > 0 ? media.VideoStreams[0].FrameRate : null,
                SourceDuration: media.Duration,
                LanguagesAlreadyInSource: alreadyPresent,
                Config: acq
            );

            return await subtitleAcquisitionService.AcquireAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "[{CorrelationId}] Subtitle acquisition failed — continuing without acquired subs",
                "unknown"
            );
            return [];
        }
    }

    private async Task<OutputPlan> BuildOutputPlanAsync(
        EncodingProfile profile,
        MediaInfo media,
        ResolvedCodec[] resolvedCodecs,
        string? cropFilter,
        EncodingContext context,
        long? probedYieldKbps,
        CancellationToken ct
    )
    {
        OutputFormat outputFormat = PlanStageHelpers.ContainerToOutputFormat(profile.Container);
        bool hlsUsesFmp4Segments = profile.Container is Container.HlsFmp4 or Container.AudioHlsFmp4;

        // Resolve tonemap strategy once — shared across all video outputs that need HDR→SDR.
        // When HdrPolicy is AlwaysPreserve, skip tonemapping entirely regardless of source.
        bool sourceIsHdr = media.VideoStreams.Count > 0 && media.VideoStreams[0].IsHdr;
        bool tonemapSuppressed = profile.HdrPolicies == HdrPolicies.AlwaysPreserve;

        TonemapStrategy? tonemap =
            sourceIsHdr && !tonemapSuppressed
                ? tonemapSelector.SelectBest(hardware, ffmpegCapabilities)
                : null;

        // Per-profile plan: resolves algorithm + nits + optional LUT from HdrOptions.
        // V2 profile has no TonemapAlgorithm shorthand — pass null; HdrOptions takes over.
        TonemapPlan tonemapPlan = await tonemapSelector
            .BuildAsync(profile.HdrOptions, null, context.DecisionsOrNoOp, cancellationToken: ct)
            .ConfigureAwait(false);

        VideoOutput[] videoOutputs = PlanStageHelpers.EnumerateVideo(profile);

        // EmitHdrAndSdr: same role split as in the resolver block above. Must
        // stay in lockstep so VideoOutputPlan count matches resolvedCodecs
        // count — 10-bit rung → HDR-only, 8-bit rung → SDR-only.
        if (
            profile.HdrPolicies == HdrPolicies.EmitHdrAndSdr
            && media.VideoStreams.Count > 0
            && media.VideoStreams[0].IsHdr
        )
        {
            videoOutputs = videoOutputs
                .Select(v =>
                    v.BitDepth >= 10
                        ? v with
                        {
                            ConvertHdrToSdr = false,
                        }
                        : v with
                        {
                            ConvertHdrToSdr = true,
                        }
                )
                .ToArray();
        }

        // Snapshot before the smart-copy downgrade: DolbyVisionGate must judge
        // DV survivability against the codec the profile actually targeted
        // (e.g. HEVC), never the "Copy" pseudo-codec that drives encoder
        // selection — a stream-copied HEVC+DV source still carries HEVC+DV in
        // the output file. See the primaryVideo lookup below.
        VideoOutput[] preSmartCopyVideoOutputs = videoOutputs;

        // Smart-copy downgrade — must mirror the identical call in ExecuteAsync
        // (see the lockstep comment there) so VideoOutputPlan and resolvedCodecs
        // stay aligned by index. This call site is the one that actually logs
        // the decision.
        videoOutputs = ApplySmartCopyDowngrade(
            videoOutputs,
            media,
            profile,
            cropFilter,
            context.DecisionsOrNoOp,
            probedYieldKbps
        );

        // Audio-only: skip video planning entirely when source has no video streams
        VideoOutputPlan[] videoPlan =
            media.VideoStreams.Count > 0 && videoOutputs.Length > 0
                ? videoOutputs
                    .Select(
                        (v, i) =>
                        {
                            ResolvedCodec resolved = resolvedCodecs[i];
                            EncoderInfo encoder = resolved.EncoderInfo;

                            (int outputWidth, int outputHeight) =
                                EncoderArgumentResolver.ResolveDimensions(
                                    v,
                                    media.VideoStreams[0].Width,
                                    media.VideoStreams[0].Height
                                );

                            Dictionary<string, string> extraFlags = new(
                                encoder.VendorSpecificFlags
                            );

                            // Translate the profile CRF to the encoder-native quality value.
                            string encoderHandle = resolved.FfmpegEncoderName;
                            IQualityScaler scaler =
                                qualityScalerResolver?.For(encoderHandle)
                                ?? new LinearQualityScaler();

                            QualityRange nativeRange = encoder.QualityRange;
                            int referenceMax = encoderHandle.Contains(
                                "av1",
                                StringComparison.OrdinalIgnoreCase
                            )
                                ? 63
                                : 51;

                            CodecHint hint = new(encoderHandle, v.Codec);
                            int translatedCrf = scaler.Translate(
                                v.Crf,
                                referenceMax,
                                nativeRange.Max,
                                hint
                            );

                            context.DecisionsOrNoOp.Add(
                                new(
                                    "plan",
                                    "plan.crf_translated",
                                    $"CRF {v.Crf} → {encoderHandle} quality {translatedCrf}",
                                    new
                                    {
                                        handle = encoderHandle,
                                        source = v.Crf,
                                        translated = translatedCrf,
                                    }
                                )
                            );

                            int crf = EncoderArgumentResolver.ResolveQuality(
                                translatedCrf,
                                resolved,
                                extraFlags
                            );

                            // Capped-CRF: when both a CRF target and a bitrate ceiling are set,
                            // the ceiling is enforced by -maxrate / -bufsize (a VBV cap on a
                            // quality-targeted encode), never by -b:v. Emitting -b:v alongside
                            // -crf / -cq / -qp puts the encoder into a conflicting rate-control
                            // state (libx264 silently switches to ABR; NVENC / AMF ignore or
                            // fight the quality target), so the bitrate must NOT reach the
                            // output plan as a target bitrate.
                            int outputBitrateKbps = v.BitrateKbps;
                            if (v is { Crf: > 0, BitrateKbps: > 0 })
                            {
                                int bufKbps = v.BitrateKbps * 2;
                                extraFlags["-maxrate"] = $"{v.BitrateKbps}k";
                                extraFlags["-bufsize"] = $"{bufKbps}k";
                                outputBitrateKbps = 0;
                            }

                            // HDR→HDR passthrough: preserve color metadata when source is HDR
                            // and the output keeps 10-bit without tonemapping.
                            bool preservesHdr =
                                sourceIsHdr && v is { BitDepth: >= 10, ConvertHdrToSdr: false };
                            if (preservesHdr)
                            {
                                VideoStreamInfo src = media.VideoStreams[0];
                                extraFlags["-color_primaries"] = src.ColorPrimaries ?? "bt2020";
                                extraFlags["-color_trc"] = src.ColorTransfer ?? "smpte2084";
                                extraFlags["-colorspace"] = src.ColorSpace ?? "bt2020nc";
                                extraFlags["-color_range"] = "tv";
                            }

                            // 10-bit / bit-depth policy resolution.
                            int requestedDepth = v.BitDepth;
                            bool outputTenBit;
                            string outputPixelFormat;
                            string outputEncoderName = resolved.FfmpegEncoderName;

                            if (bitDepthPolicyResolver is not null)
                            {
                                BitDepthResolutionResult bdResult = bitDepthPolicyResolver.Resolve(
                                    requestedDepth,
                                    profile.BitDepthPolicy,
                                    v.Codec,
                                    resolved,
                                    context.DecisionsOrNoOp,
                                    codecType =>
                                        codecResolver.Resolve(
                                            codecType,
                                            hardware,
                                            EncoderPreference.ForceSoftware
                                        )
                                );

                                if (bdResult.Failure is not null)
                                {
                                    // Surface Strict violation as a StageFailure — throw so
                                    // the outer catch re-wraps it, or return early by throwing.
                                    throw bdResult.Failure;
                                }

                                if (bdResult.SwitchedToEncoder is not null)
                                {
                                    // PreferSoftware swapped the encoder — adopt the SW codec.
                                    outputEncoderName = bdResult.SwitchedToEncoder;
                                    resolved = codecResolver.Resolve(
                                        v.Codec,
                                        hardware,
                                        EncoderPreference.ForceSoftware
                                    );
                                    encoder = resolved.EncoderInfo;
                                }

                                foreach (EncoderRule bdWarning in bdResult.Warnings)
                                {
                                    logger.LogWarning(
                                        "Bit-depth policy [{Id}]: {Message}",
                                        bdWarning.Id,
                                        bdWarning.Message
                                    );
                                }

                                outputTenBit = bdResult.FinalBitDepth == 10;
                                outputPixelFormat = bdResult.PixelFormat ?? "yuv420p";
                            }
                            else
                            {
                                // Legacy path — preserve existing behaviour exactly.
                                outputTenBit = requestedDepth >= 10 && encoder.Supports10Bit;
                                if (requestedDepth >= 10 && !encoder.Supports10Bit)
                                {
                                    logger.LogWarning(
                                        "Profile requests 10-bit video_{Index} but encoder {Encoder} does not support 10-bit. Downgrading to 8-bit output.",
                                        i,
                                        encoder.FfmpegName
                                    );
                                }

                                outputPixelFormat = outputTenBit
                                    ? encoder.PixelFormat10Bit
                                    : "yuv420p";
                            }

                            string? codecProfileString =
                                v.CodecProfile == CodecProfile.Auto
                                    ? null
                                    : v.CodecProfile.ToString().ToLowerInvariant();

                            // Profile/plugin CustomArguments escape hatch: merge the
                            // per-video custom flags last so user/plugin intent wins.
                            // Defense in depth: even though the validator now rejects
                            // reserved-flag overrides, a code-built or legacy-validated
                            // profile could still carry one. Skip any key a typed field
                            // already owns so the argv can never emit a duplicate /
                            // conflicting flag next to the value the resolver computed.
                            if (v.CustomArguments is not null)
                            {
                                foreach ((string argKey, string argValue) in v.CustomArguments)
                                {
                                    string normalizedKey = argKey.StartsWith('-')
                                        ? argKey
                                        : $"-{argKey}";
                                    if (ProfileRuleValidator.ReservedFlags.Contains(normalizedKey))
                                        continue;
                                    extraFlags[argKey] = argValue;
                                }
                            }

                            return new VideoOutputPlan(
                                Width: outputWidth,
                                Height: outputHeight,
                                EncoderName: outputEncoderName,
                                Crf: crf,
                                BitrateKbps: outputBitrateKbps,
                                Preset: EncoderArgumentResolver.ResolvePreset(v.Preset, encoder),
                                Profile: EncoderArgumentResolver.ResolveProfile(
                                    codecProfileString,
                                    encoder,
                                    outputTenBit ? 10 : 8
                                ),
                                Level: v.Level,
                                TenBit: outputTenBit,
                                PixelFormat: outputPixelFormat,
                                // Copy outputs must map directly from the input stream.
                                // A bracketed label like [v0] forces ffmpeg to route the
                                // stream through filter_complex, which is forbidden when
                                // the output codec is "copy" (exit -22). Direct stream
                                // references (0:v:0) bypass filter_complex entirely.
                                MapLabel: v.Policy == StreamPolicy.Copy ? "0:v:0" : $"[v{i}]",
                                ExtraFlags: extraFlags,
                                FrameRate: media.VideoStreams[0].FrameRate,
                                SegmentNameTemplate: v.SegmentNameTemplate,
                                PlaylistNameTemplate: v.PlaylistNameTemplate,
                                ConvertHdrToSdr: v.ConvertHdrToSdr && sourceIsHdr,
                                TonemapFilterChain: v.ConvertHdrToSdr
                                && sourceIsHdr
                                && !tonemapSuppressed
                                    ? tonemapPlan.FilterStringFragment
                                    : null,
                                CropFilter: cropFilter,
                                IsHdrOutput: preservesHdr,
                                // Every rung here is built from media.VideoStreams[0] —
                                // the source stream this output was planned against. See
                                // VideoOutputPlan.SourceStreamIndex.
                                SourceStreamIndex: media.VideoStreams[0].Index
                            );
                        }
                    )
                    .ToArray()
                : [];

        // Disambiguate any video plans whose templates would resolve to the
        // same on-disk path. The HdrPolicy.EmitHdrAndSdr expansion plus
        // H264FallbackHeights can produce two 1080p rungs (HEVC tonemap + H.264
        // fallback) that both label as "video_1920x1080_SDR" via TemplateResolver.
        // Append a codec-family token to colliding entries so they land in
        // distinct directories (video_1920x1080_SDR_avc/ vs _hevc/).
        videoPlan = PlanStageDisambiguation.DisambiguateVideo(videoPlan);

        // Smart-copy downgrade — audio equivalent of ApplySmartCopyDowngrade
        // above. Unlike video, there is no separate resolvedCodecs array to
        // keep in lockstep with (audio never goes through hardware codec
        // resolution), so this runs exactly once, right before the plan is
        // built from it.
        AudioOutput[] audioOutputs = ApplySmartCopyDowngradeAudio(
            profile.Audio,
            media,
            profile,
            context.DecisionsOrNoOp
        );

        // Build one AudioOutputPlan per matching source stream.
        // AllowedLanguages is a FILTER — the actual language comes from the source stream.
        AudioOutputPlan[] audioPlan = AudioPlanBuilder.Build(
            profile with
            {
                Audio = audioOutputs,
            },
            media
        );

        SubtitleOutputPlan[] subtitlePlan = SubtitlePlanBuilder.Build(profile, media);

        ThumbnailOutputPlan? thumbPlan = ThumbnailPlanBuilder.Build(profile, media);

        // Clamp segment duration to input length for very short files.
        int segmentDuration = profile.SegmentDurationSeconds;
        if (media.Duration.TotalSeconds > 0 && media.Duration.TotalSeconds < segmentDuration)
            segmentDuration = Math.Max(1, (int)Math.Ceiling(media.Duration.TotalSeconds));

        // Dolby Vision passthrough gate.
        // Per-output bit-depth: evaluate using the first video output because
        // DV RPU is a stream-level property. Reads the pre-smart-copy snapshot
        // (see above) so a rung the smart-copy downgrade routed through "Copy"
        // is still judged against its real target codec.
        VideoOutput? primaryVideo =
            preSmartCopyVideoOutputs.Length > 0 ? preSmartCopyVideoOutputs[0] : null;
        int primaryBitDepth = primaryVideo?.BitDepth ?? 8;
        VideoCodecType primaryCodec = primaryVideo?.Codec ?? VideoCodecType.H264;

        // Dolby Vision RPU survives only a bitstream copy — a re-encode (any
        // crop / scale / tonemap, or nvenc at all) strips it, so DV may only be
        // preserved when the primary video is stream-copied.
        bool videoIsCopy =
            (videoOutputs.Length > 0 && videoOutputs[0].Policy == StreamPolicy.Copy)
            || videoOutputs.Length == 0;

        DolbyVisionDecision dvDecision = DolbyVisionGate.Resolve(
            media.DolbyVision,
            primaryCodec,
            primaryBitDepth,
            outputFormat,
            profile.HdrPolicies,
            videoIsCopy,
            context.DecisionsOrNoOp,
            hlsUsesFmp4Segments
        );

        // Merge DV container flags into the first video output's ExtraFlags.
        if (dvDecision is { Preserved: true, ExtraFlags.Count: > 0 } && videoPlan.Length > 0)
        {
            foreach ((string key, string value) in dvDecision.ExtraFlags)
                videoPlan[0].ExtraFlags[key] = value;
        }

        if (media.DolbyVision is not null && !dvDecision.Preserved)
        {
            logger.LogWarning(
                "Source has Dolby Vision (profile {Profile}.{Level}) but DV will be stripped — {Reason}",
                media.DolbyVision.Profile,
                media.DolbyVision.Level,
                dvDecision.Reason
            );
        }

        // 3D stereo_mode preservation: when the source has a stereo_mode tag and
        // the profile stream-copies video, forward the tag so the muxer carries it
        // to the output. Transcode paths cannot preserve it (rejected by validator).
        // Reads the post-downgrade videoOutputs[0] (not profile.Video) so a rung
        // the smart-copy downgrade routed through Copy also gets the tag — the
        // author-declared always-copy preset still resolves the same way since
        // its single VideoOutput carries Policy: Copy from the start.
        // (videoIsCopy is computed above, before the Dolby Vision gate.)
        if (media.StereoMode is not null && videoIsCopy && videoPlan.Length > 0)
        {
            // MKV: -metadata:s:v stereo_mode=<value> tags the video track — the
            // flag and the key=value pair are separate argv tokens, so the dict
            // key must be just the flag, not "flag key" glued together.
            // MP4: stream-copy keeps the st3d box automatically when -c:v copy is
            //      used; the extra tag does not hurt non-MKV containers.
            videoPlan[0].ExtraFlags["-metadata:s:v"] = $"stereo_mode={media.StereoMode}";
        }

        // VR spherical projection preservation: pass-through the sv3d/proj box
        // metadata on stream-copy. -movflags +write_colr ensures the MP4 muxer
        // emits colour information that VR players expect alongside sv3d.
        if (media.SphericalProjection is not null && videoIsCopy && videoPlan.Length > 0)
        {
            if (outputFormat is OutputFormat.Mp4 or OutputFormat.Hls or OutputFormat.Dash)
            {
                videoPlan[0].ExtraFlags["-movflags"] = "+write_colr";
            }

            logger.LogInformation(
                "[{CorrelationId}] VR source (projection={Projection}): stream-copy preserves spherical metadata",
                context.CorrelationId,
                media.SphericalProjection
            );
        }

        // Resolve HLS muxer options from V2 HlsConfig + Container. Strategies
        // need flat strings ("mpegts" | "fmp4", "vod" | "event") so we materialise
        // them here once instead of teaching every strategy how to read V2.
        HlsPlanOptions? hlsOptions = profile.Container
            is Container.HlsTs
                or Container.HlsFmp4
                or Container.AudioHlsTs
                or Container.AudioHlsFmp4
            ? new HlsPlanOptions(
                SegmentType: hlsUsesFmp4Segments ? "fmp4" : "mpegts",
                PlaylistType: profile.Hls?.PlaylistType == HlsPlaylistType.Event ? "event" : "vod",
                IndependentSegments: profile.Hls?.IndependentSegments ?? true
            )
            : null;

        BundleLayout? layout =
            outputNamingResolver is not null && context.MediaItem is not null
                ? outputNamingResolver.Resolve(context.MediaItem, profile)
                : null;

        HlsDerivatives effectiveDerivatives = profile.HlsDerivatives ?? new HlsDerivatives();
        bool generateChapterThumbs = effectiveDerivatives.GenerateChapterThumbs;
        bool emitSubtitleChunks = effectiveDerivatives.SubtitleWebVtt;

        return new(
            outputFormat,
            videoPlan,
            audioPlan,
            subtitlePlan,
            thumbPlan,
            segmentDuration,
            PreserveDolbyVision: dvDecision.Preserved,
            Drm: DrmConfigConverter.Convert(profile.Drm),
            HlsOptions: hlsOptions,
            Layout: layout,
            GenerateChapterThumbs: generateChapterThumbs,
            EmitSubtitleWebVttChunks: emitSubtitleChunks,
            GlobalExtraFlags: BuildGlobalExtraFlags(profile.CustomArguments, context.Resources),
            NormalizeToConstantFrameRate: media.VideoStreams.Count > 0
                && media.VideoStreams[0].IsVariableFrameRate
        );
    }

    // Cap ffmpeg's global thread count so concurrent encodes never collectively
    // ask the OS for more threads than IResourceBudget actually reserved for
    // them. When this run is a coordinator's decomposed task, resources carries
    // the exact CpuThreads TaskResourceHelper sized for it at decompose time —
    // the same number the budget semaphore gated concurrency on — so this must
    // match it exactly, not compute an independent "quarter of the host" share:
    // that mismatch let N budget-compliant concurrent tasks each spawn a
    // full-width ffmpeg process and peg the host regardless of N. No task
    // context (preview, live-transcode) falls back to the flat host-fraction
    // default. The user's own -threads in CustomArguments overrides either
    // (user override is non-negotiable).
    private static Dictionary<string, string> BuildGlobalExtraFlags(
        Dictionary<string, string>? customArguments,
        ResourceRequirement? resources
    )
    {
        int encodeThreads;
        if (resources is { CpuThreads: > 0 })
        {
            encodeThreads = resources.CpuThreads;
        }
        else
        {
            int reserved = Math.Max(1, Environment.ProcessorCount / 4);
            encodeThreads = Math.Max(1, Environment.ProcessorCount - reserved);
        }

        Dictionary<string, string> flags = new() { ["-threads"] = encodeThreads.ToString() };

        if (customArguments is { Count: > 0 })
            foreach ((string key, string value) in customArguments)
                flags[key] = value;

        return flags;
    }
}
