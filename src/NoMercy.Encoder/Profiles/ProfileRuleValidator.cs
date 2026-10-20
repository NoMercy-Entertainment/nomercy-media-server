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
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Errors;

namespace NoMercy.Encoder.Profiles;

/// <summary>
///     Structured rule emitter for encoding profiles. Returns an
///     <see cref="ValidationEnvelope"/> whose entries are typed
///     <see cref="EncoderRule"/> records with stable IDs from
///     <see cref="EncoderRuleId"/>, the spec field path, a human-readable message,
///     and a concrete fix.
///     <para>
///         Replaces the string-based <see cref="ProfileValidator.Validate"/> for new callers.
///         The legacy method stays for backward compatibility while consumers migrate.
///     </para>
/// </summary>
public static class ProfileRuleValidator
{
    /// <summary>
    ///     Run every static rule against the profile alone (no source media). Use
    ///     <see cref="ValidateWithSource"/> to layer source-dependent rules on top
    ///     (HFR level cap, stereoscopic, spherical).
    /// </summary>
    public static ValidationEnvelope Validate(EncodingProfile profile)
    {
        List<EncoderRule> rules = [];

        EmitProfileNameMissing(profile, rules);
        EmitProfileNoOutputs(profile, rules);
        EmitVideoWidthInvalid(profile, rules);
        EmitVideoHeightInvalid(profile, rules);
        EmitVideoRateControlMissing(profile, rules);
        EmitVideoRateControlConflict(profile, rules);
        EmitCodecContainerMismatch(profile, rules);
        EmitAudioCodecContainerMismatch(profile, rules);
        EmitAudioBitrateMissing(profile, rules);
        EmitHlsFmp4CodecMismatch(profile, rules);
        EmitLadderDuplicateVariant(profile, rules);
        EmitLadderManualEmpty(profile, rules);
        EmitLadderManualUnsorted(profile, rules);
        EmitProfileLevelResolutionMismatch(profile, rules);
        EmitLevelInvalid(profile, rules);
        EmitBitrateTooLowForResolution(profile, rules);
        EmitCrfOutOfTypicalRange(profile, rules);
        EmitHlsKeyframeSegmentMisalignment(profile, rules);
        EmitLadderInverted(profile, rules);
        EmitAudioAc3OffLadderBitrate(profile, rules);
        EmitSubtitlesContainerIncompatible(profile, rules);
        EmitSubtitlesBurnInPermanent(profile, rules);
        EmitSubtitlesAssNeedsCapableClient(profile, rules);
        EmitHdrInverseTonemapUnsupported(profile, rules);
        EmitBitDepthVp9ProfileMismatch(profile, rules);
        EmitBitDepthH26xProfilePromoted(profile, rules);
        EmitCustomArgsReservedFlag(profile, rules);
        EmitDrmHttpNotHttps(profile, rules);
        EmitDrmKeyMissing(profile, rules);

        return ValidationEnvelope.FromRules(rules);
    }

    /// <summary>
    ///     The full set of ffmpeg flags whose values are derived from the profile's typed fields
    ///     and must not be hand-overridden via <see cref="EncodingProfile.CustomArguments"/>.
    ///     Letting users smuggle these through silently desyncs the validator from what ffmpeg
    ///     actually runs (the profile says one thing, the encode does another). Spec part 04
    ///     §"reserved flags".
    /// </summary>
    public static readonly IReadOnlySet<string> ReservedFlags = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        // Codec selection — driven by VideoOutput.Codec / AudioOutput.Codec / SubtitleOutput.Codec
        "-c:v",
        "-c:a",
        "-c:s",
        "-vcodec",
        "-acodec",
        "-scodec",
        // Container muxer — driven by EncodingProfile.Container
        "-f",
        // Encoder tuning — driven by VideoOutput.Preset
        "-preset",
        // Hardware pipeline — chosen by HardwarePreferenceResolver based on the profile policy
        "-init_hw_device",
        "-filter_hw_device",
        "-hwaccel",
        "-hwaccel_output_format",
        // Stream mapping — derived from the policy + ladder + audio/subtitle selection
        "-map",
        "-map_metadata",
        // Filter graph — built by FilterGraphBuilder from crop / scale / tonemap settings
        "-vf",
        "-af",
        "-filter_complex",
        // HLS muxer arguments — driven by HlsConfig + SegmentDurationSeconds
        "-hls_time",
        "-hls_segment_filename",
        "-hls_playlist_type",
        "-hls_segment_type",
        // Rate control — driven by VideoOutput.RateControl / Crf / BitrateKbps.
        // The resolver owns the whole rate-control flag set as a unit; letting a
        // custom flag override one member desyncs it from the others (e.g. a
        // custom -rc cbr on top of the resolver's -cq, or a custom -maxrate that
        // discards the computed VBV ceiling while -bufsize stays derived).
        "-crf",
        "-b:v",
        "-maxrate",
        "-bufsize",
        "-rc",
        "-cq",
        "-qp",
        "-global_quality",
        "-q:v",
        // Codec profile / level / pixel format — driven by VideoOutput.CodecProfile,
        // Level, BitDepth. A custom override is emitted a SECOND time next to the
        // typed emit (ffmpeg last-wins), so the manifest advertises one thing and
        // the stream is another.
        "-profile:v",
        "-level",
        "-pix_fmt",
        "-profile:a",
        // GOP — driven by KeyframeIntervalSeconds; the strategy always emits -g.
        "-g",
        "-keyint_min",
        // Audio shaping — driven by AudioOutput.BitrateKbps / SampleRateHz / Channels.
        "-b:a",
        "-ar",
        "-ac",
        // HDR / color signaling — derived from the source when HDR passthrough is
        // preserved; a custom override produces inconsistent primaries/trc/matrix.
        "-color_primaries",
        "-color_trc",
        "-colorspace",
        "-color_range",
        // Stream tags — driven by the codec/container (hvc1/dvh1 for HEVC/DV).
        "-tag:v",
    };

    /// <summary>
    ///     Layer source-dependent rules on top of <see cref="Validate"/>. Returns a combined envelope.
    /// </summary>
    public static ValidationEnvelope ValidateWithSource(EncodingProfile profile, MediaInfo source)
    {
        ValidationEnvelope baseEnvelope = Validate(profile);
        List<EncoderRule> sourceRules = [];

        EmitLevelFrameRateCapExceeded(profile, source, sourceRules);
        EmitSourceVariableFrameRate(profile, source, sourceRules);
        EmitSourceDolbyVisionWillBeStripped(profile, source, sourceRules);
        EmitSourceUpscalingDetected(profile, source, sourceRules);
        EmitStereoscopicSourceUnsupported(profile, source, sourceRules);
        EmitSphericalMetadataWillBeStripped(profile, source, sourceRules);

        return ValidationEnvelope.FromRules(
            baseEnvelope.Errors.Concat(baseEnvelope.Warnings).Concat(sourceRules)
        );
    }

    // ----------------------------------------------------------------------
    // Structural profile rules
    // ----------------------------------------------------------------------

    private static void EmitProfileNameMissing(EncodingProfile profile, List<EncoderRule> rules)
    {
        if (!string.IsNullOrWhiteSpace(profile.Name))
            return;

        rules.Add(
            new(
                Id: EncoderRuleId.ProfileNameMissing,
                Severity: EncoderRuleSeverity.Error,
                Field: "name",
                Message: "Profile name is required so operators can identify the preset in the dashboard.",
                Fix: "Set a non-empty profile.name (e.g. \"General 1080p Fast\")."
            )
        );
    }

    private static void EmitProfileNoOutputs(EncodingProfile profile, List<EncoderRule> rules)
    {
        // A profile must produce at least one output stream — every encode call would be a no-op.
        // Audio/Subtitles may be null when Newtonsoft.Json deserialises a positional record whose
        // JSON omits those fields — guard so we emit the rule rather than throw.
        bool hasVideo = profile.Video is { Policy: not StreamPolicy.Omit };
        bool hasAudio = (profile.Audio ?? []).Any(a => a.Policy != StreamPolicy.Omit);
        bool hasSubtitles = (profile.Subtitles ?? []).Any(s => s.Policy != SubtitlePolicy.Omit);

        if (hasVideo || hasAudio || hasSubtitles)
            return;

        rules.Add(
            new(
                Id: EncoderRuleId.ProfileNoOutputs,
                Severity: EncoderRuleSeverity.Error,
                Field: "outputs",
                Message: "Profile declares no video, audio, or subtitle outputs; an encode run "
                    + "would produce no streams.",
                Fix: "Add at least one VideoOutput, AudioOutput, or SubtitleOutput with policy != Skip / Omit."
            )
        );
    }

    private static void EmitVideoWidthInvalid(EncodingProfile profile, List<EncoderRule> rules)
    {
        // Null means "keep source width" — a valid, deliberate request (e.g. an
        // archive preset that re-encodes the codec without rescaling). Only a
        // width that IS set but is not positive is an error.
        if (profile.Video is not { Policy: StreamPolicy.Transcode } video || video.Width is null)
            return;

        if (video.Width.Value > 0)
            return;

        rules.Add(
            new(
                Id: EncoderRuleId.VideoWidthInvalid,
                Severity: EncoderRuleSeverity.Error,
                Field: "video.width",
                Message: $"Video width must be null (keep source) or a positive integer "
                    + $"(got {video.Width}); the encoder cannot produce a 0-pixel-wide output.",
                Fix: "Set video.width to a positive value (typical values: 854, 1280, 1920, "
                    + "2560, 3840), or leave it null to keep the source width."
            )
        );
    }

    private static void EmitVideoHeightInvalid(EncodingProfile profile, List<EncoderRule> rules)
    {
        if (profile.Video is not { Policy: StreamPolicy.Transcode } video || video.Height is null)
            return;

        if (video.Height.Value > 0)
            return;

        rules.Add(
            new(
                Id: EncoderRuleId.VideoHeightInvalid,
                Severity: EncoderRuleSeverity.Error,
                Field: "video.height",
                Message: $"Video height must be positive when set (got {video.Height}); "
                    + "leave it null to derive from source aspect ratio.",
                Fix: "Set video.height to a positive integer or null."
            )
        );
    }

    private static void EmitVideoRateControlMissing(
        EncodingProfile profile,
        List<EncoderRule> rules
    )
    {
        // Every transcode needs a quality target — either a CRF value or a bitrate. A profile that
        // pinned RateControlMode.Crf but left Crf at 0 (default) silently encodes near-lossless and
        // produces enormous files. A profile that pinned Vbr / Cbr but left BitrateKbps at 0 has
        // no target at all and the muxer falls back to whatever the encoder default is.
        if (profile.Video is not { Policy: StreamPolicy.Transcode } video)
            return;

        bool isCrfMode = video.RateControl == RateControlMode.Crf;
        bool isBitrateMode =
            video.RateControl == RateControlMode.Vbr || video.RateControl == RateControlMode.Cbr;

        bool crfMissing = isCrfMode && video.Crf <= 0;
        bool bitrateMissing = isBitrateMode && video.BitrateKbps <= 0;

        if (!crfMissing && !bitrateMissing)
            return;

        string field = isCrfMode ? "video.crf" : "video.bitrate_kbps";
        string mode = isCrfMode ? "Crf" : video.RateControl.ToString();

        rules.Add(
            new(
                Id: EncoderRuleId.VideoRateControlMissing,
                Severity: EncoderRuleSeverity.Error,
                Field: field,
                Message: $"Rate control is {mode} but {field} is unset; the encoder has no quality "
                    + "target and would emit a stream nobody asked for.",
                Fix: isCrfMode
                    ? "Set video.crf to a value in 17..28 (23 is the typical default)."
                    : "Set video.bitrate_kbps to a positive value matched to the resolution."
            )
        );
    }

    private static void EmitVideoRateControlConflict(
        EncodingProfile profile,
        List<EncoderRule> rules
    )
    {
        if (profile.Video is not { Policy: StreamPolicy.Transcode } video)
            return;

        // ABR / VBR / CBR want a positive bitrate; pure CRF wants the CRF value populated.
        bool isBitrateMode =
            video.RateControl == RateControlMode.Vbr || video.RateControl == RateControlMode.Cbr;
        bool hasBitrate = video.BitrateKbps > 0;
        bool hasCrf = video.Crf > 0;

        if (isBitrateMode && hasCrf && !hasBitrate)
        {
            rules.Add(
                new(
                    Id: EncoderRuleId.VideoRateControlConflict,
                    Severity: EncoderRuleSeverity.Error,
                    Field: "video.rate_control",
                    Message: $"Rate control is {video.RateControl} (bitrate-targeted) but bitrate_kbps "
                        + $"is unset and crf is {video.Crf}; the encoder cannot decide which value to honour.",
                    Fix: "Either set bitrate_kbps and keep rate_control at VBR/CBR, "
                        + "or change rate_control to Crf and clear bitrate_kbps."
                )
            );
        }
    }

    private static void EmitCodecContainerMismatch(EncodingProfile profile, List<EncoderRule> rules)
    {
        if (profile.Video is not { Policy: StreamPolicy.Transcode } video)
            return;

        if (ContainerCompatibility.SupportsVideo(profile.Container, video.Codec))
            return;

        rules.Add(
            new(
                Id: EncoderRuleId.CodecContainerMismatch,
                Severity: EncoderRuleSeverity.Error,
                Field: "video.codec",
                Message: $"Container {profile.Container} does not support video codec {video.Codec}; "
                    + "the muxer will refuse the stream.",
                Fix: $"Change video.codec or pick a container that supports {video.Codec} "
                    + "(see the codec/container matrix in The Effortless Encoder part 02)."
            )
        );
    }

    private static void EmitAudioCodecContainerMismatch(
        EncodingProfile profile,
        List<EncoderRule> rules
    )
    {
        foreach (
            AudioOutput audio in (profile.Audio ?? []).Where(a =>
                a.Policy == StreamPolicy.Transcode
            )
        )
        {
            if (ContainerCompatibility.SupportsAudio(profile.Container, audio.Codec))
                continue;

            rules.Add(
                new(
                    Id: EncoderRuleId.AudioCodecContainerMismatch,
                    Severity: EncoderRuleSeverity.Error,
                    Field: "audio.codec",
                    Message: $"Container {profile.Container} does not support audio codec {audio.Codec}.",
                    Fix: $"Change audio.codec or pick a container that supports {audio.Codec}."
                )
            );
        }
    }

    private static void EmitAudioBitrateMissing(EncodingProfile profile, List<EncoderRule> rules)
    {
        // Lossy audio encoders need a target bitrate. FLAC and TrueHD are lossless and
        // ignore -b:a — leave those alone. Copy-mode audio is a passthrough.
        foreach (
            AudioOutput audio in (profile.Audio ?? []).Where(a =>
                a.Policy == StreamPolicy.Transcode
            )
        )
        {
            if (audio.Codec is AudioCodecType.Flac or AudioCodecType.TrueHd)
                continue;
            if (audio.BitrateKbps > 0)
                continue;

            rules.Add(
                new(
                    Id: EncoderRuleId.AudioBitrateMissing,
                    Severity: EncoderRuleSeverity.Error,
                    Field: "audio.bitrate_kbps",
                    Message: $"Audio output for codec {audio.Codec} has bitrate_kbps={audio.BitrateKbps}; "
                        + "lossy encoders require a positive target bitrate.",
                    Fix: $"Set audio.bitrate_kbps to a positive value (typical: "
                        + $"{(audio.Codec is AudioCodecType.Aac ? "128–256" : "192–448")} kbps)."
                )
            );
        }
    }

    private static void EmitLadderManualEmpty(EncodingProfile profile, List<EncoderRule> rules)
    {
        if (profile.Ladder is not { Mode: LadderMode.Manual } ladder)
            return;
        if (ladder.Rungs is { Length: > 0 })
            return;

        rules.Add(
            new(
                Id: EncoderRuleId.LadderManualEmpty,
                Severity: EncoderRuleSeverity.Error,
                Field: "ladder.rungs",
                Message: "Manual ladder mode requires at least one rung; rungs[] is empty.",
                Fix: "Add at least one LadderRung entry, or switch to LadderMode.Auto."
            )
        );
    }

    private static void EmitLadderManualUnsorted(EncodingProfile profile, List<EncoderRule> rules)
    {
        if (profile.Ladder is not { Mode: LadderMode.Manual } ladder)
            return;
        if (ladder.Rungs is not { Length: > 1 } rungs)
            return;

        for (int i = 1; i < rungs.Length; i++)
        {
            if (rungs[i].BitrateKbps > rungs[i - 1].BitrateKbps)
                continue;

            rules.Add(
                new(
                    Id: EncoderRuleId.LadderManualUnsorted,
                    Severity: EncoderRuleSeverity.Error,
                    Field: "ladder.rungs",
                    Message: $"Manual ladder rungs must be sorted ascending by bitrate; "
                        + $"rung[{i}] ({rungs[i].BitrateKbps} kbps) <= rung[{i - 1}] "
                        + $"({rungs[i - 1].BitrateKbps} kbps).",
                    Fix: "Reorder rungs by increasing bitrate_kbps so HLS variant negotiation "
                        + "selects the right rung at each bandwidth."
                )
            );
            return;
        }
    }

    private static void EmitHlsFmp4CodecMismatch(EncodingProfile profile, List<EncoderRule> rules)
    {
        // HlsTs only carries H.264 per Apple HLS Authoring Specification §1.5. The codec/container
        // matrix already enforces this at SupportsVideo, but we want a dedicated rule with a stable
        // ID so the dashboard can deep-link the HLS-fmp4 explanation.
        if (
            profile.Container != Container.HlsTs
            || profile.Video is not { Policy: StreamPolicy.Transcode } video
            || video.Codec == VideoCodecType.H264
        )
            return;

        rules.Add(
            new(
                Id: EncoderRuleId.HlsFmp4CodecMismatch,
                Severity: EncoderRuleSeverity.Error,
                Field: "container",
                Message: $"HLS MPEG-TS only carries H.264 (Apple HLS Authoring §1.5); "
                    + $"video.codec is {video.Codec}.",
                Fix: "Switch container to HlsFmp4 for HEVC / AV1, or change video.codec to H264."
            )
        );
    }

    private static void EmitLadderDuplicateVariant(EncodingProfile profile, List<EncoderRule> rules)
    {
        if (profile.Ladder is not { Mode: LadderMode.Manual, Rungs: { Length: > 1 } rungs })
            return;

        HashSet<string> seen = [];
        for (int i = 0; i < rungs.Length; i++)
        {
            string key =
                $"{rungs[i].Codec}|{rungs[i].Width}x{rungs[i].Height}|{rungs[i].BitrateKbps}";
            if (seen.Add(key))
                continue;

            rules.Add(
                new(
                    Id: EncoderRuleId.LadderDuplicateVariant,
                    Severity: EncoderRuleSeverity.Warning,
                    Field: $"ladder.rungs[{i}]",
                    Message: $"Ladder rung {i} duplicates an earlier rung ({rungs[i].Codec} "
                        + $"{rungs[i].Width}x{rungs[i].Height} @ {rungs[i].BitrateKbps} kbps); "
                        + "the second copy is wasted CPU.",
                    Fix: $"Remove or differentiate rung {i} (change bitrate, resolution, or codec)."
                )
            );
            return;
        }
    }

    private static void EmitSubtitlesAssNeedsCapableClient(
        EncodingProfile profile,
        List<EncoderRule> rules
    )
    {
        // ASS / SSA typesetting renders correctly only on players that ship a libass-backed
        // engine. Most browsers don't, so an HLS extract-mode ASS track is silently fallback-
        // rendered as plain text on Safari / Edge / Firefox without a player extension.
        bool isHlsExtract =
            profile.Container
            is Container.HlsTs
                or Container.HlsFmp4
                or Container.AudioHlsTs
                or Container.AudioHlsFmp4
                or Container.Dash;

        foreach (
            SubtitleOutput subtitle in (profile.Subtitles ?? []).Where(s =>
                s is { Codec: SubtitleCodecType.Ass, Policy: SubtitlePolicy.Extract }
            )
        )
        {
            if (!isHlsExtract)
                continue;

            rules.Add(
                new(
                    Id: EncoderRuleId.SubtitlesAssNeedsCapableClient,
                    Severity: EncoderRuleSeverity.Info,
                    Field: "subtitles.codec",
                    Message: $"ASS subtitle extracted to {profile.Container} is rendered with typesetting "
                        + "only on players that bundle a libass-compatible engine; most browsers fall "
                        + "back to plain text without positioning, fonts, or effects.",
                    Fix: "Use the SubtitlesOctopus plugin in the web player, ship ASS rendering at the client, "
                        + "or set subtitles.policy to BurnIn for guaranteed-fidelity playback."
                )
            );
            return;
        }
    }

    private static void EmitDrmKeyMissing(EncodingProfile profile, List<EncoderRule> rules)
    {
        // DRM enabled but no key delivery configured: HLS AES-128 needs a key_uri the client can
        // fetch the raw key from, CENC needs an inline key id + value or a license server URL.
        // Without either, packaging fails late.
        if (profile.Drm is null)
            return;

        string scheme = (profile.Drm.Scheme ?? string.Empty).ToLowerInvariant();
        if (scheme is "" or "none")
            return;

        // The same lookup the plan converter uses, so a key this rule accepts is
        // one the packager receives.
        if (DrmKeyParameters.FindKeyUri(profile.Drm.Parameters) is null)
            rules.Add(BuildDrmKeyMissingRule(scheme, "drm.parameters"));
    }

    private static EncoderRule BuildDrmKeyMissingRule(string scheme, string field) =>
        new(
            Id: EncoderRuleId.DrmKeyMissing,
            Severity: EncoderRuleSeverity.Error,
            Field: field,
            Message: $"DRM scheme '{scheme}' is enabled but no key delivery URI is configured; "
                + "packaging will fail at encode time.",
            Fix: "Add a key_uri / license_url entry to drm.parameters pointing to "
                + "the key server, or set drm.scheme to 'none' to disable DRM."
        );

    private static void EmitDrmHttpNotHttps(EncodingProfile profile, List<EncoderRule> rules)
    {
        // DRM key URLs must travel over HTTPS — plain HTTP exposes the decryption key to any
        // on-path observer. Spec part 07 §"DRM" + part 10 §"HTTPS enforcement".
        if (profile.Drm?.Parameters is null)
            return;

        foreach ((string key, string value) in profile.Drm.Parameters)
        {
            string normalisedKey = key.ToLowerInvariant();
            bool looksLikeKeyUri =
                normalisedKey is "key_uri" or "key_url" or "keyuri" or "license_url";
            if (!looksLikeKeyUri || string.IsNullOrWhiteSpace(value))
                continue;

            if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                continue;
            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                rules.Add(
                    new(
                        Id: EncoderRuleId.DrmHttpNotHttps,
                        Severity: EncoderRuleSeverity.Error,
                        Field: $"drm.parameters[{key}]",
                        Message: $"DRM key URI uses plain HTTP ({value}); the decryption key would "
                            + "travel in cleartext to every on-path observer.",
                        Fix: "Switch the key URI scheme to https:// or terminate TLS at a "
                            + "reverse proxy in front of the key endpoint."
                    )
                );
            }
        }
    }

    // ----------------------------------------------------------------------
    // Source-dependent rule emitters
    // ----------------------------------------------------------------------

    private static void EmitSourceVariableFrameRate(
        EncodingProfile profile,
        MediaInfo source,
        List<EncoderRule> rules
    )
    {
        if (!source.IsVariableFrameRate)
            return;
        if (profile.Video is not { Policy: StreamPolicy.Transcode })
            return;

        rules.Add(
            new(
                Id: EncoderRuleId.SourceVariableFrameRate,
                Severity: EncoderRuleSeverity.Warning,
                Field: "source.frame_rate",
                Message: "Source is variable frame rate (VFR); the encoder will resample to a "
                    + "constant frame rate. Long-duration content can drift up to ~1 second per hour.",
                Fix: "If drift is unacceptable, set video.policy to Copy to preserve the source "
                    + "frame-rate timing, or convert via -fps_mode passthrough manually."
            )
        );
    }

    private static void EmitSourceUpscalingDetected(
        EncodingProfile profile,
        MediaInfo source,
        List<EncoderRule> rules
    )
    {
        // Upscaling a low-res source to a higher target resolution wastes bytes without adding
        // detail. The encoder will produce the larger output but it carries the same information
        // as the source — and a poorly-tuned bitrate ladder can hide quality regressions behind
        // the resolution change.
        // A null (or legacy 0) width means "keep source width" — that can never
        // upscale, so there is nothing to warn about.
        if (
            profile.Video is not { Policy: StreamPolicy.Transcode } video
            || video.Width is not int width
            || width <= 0
        )
            return;
        if (source.VideoStreams.Count == 0)
            return;

        VideoStreamInfo primary = source.VideoStreams[0];
        if (primary.Width <= 0 || width <= primary.Width)
            return;

        rules.Add(
            new(
                Id: EncoderRuleId.SourceUpscalingDetected,
                Severity: EncoderRuleSeverity.Warning,
                Field: "video.width",
                Message: $"Target width {width} exceeds source width {primary.Width}; "
                    + "the encoder will upscale, adding bytes without adding detail.",
                Fix: $"Lower video.width to {primary.Width} (or below) to avoid wasted bitrate, "
                    + "or accept the upscale if the larger frame is needed for player compatibility."
            )
        );
    }

    private static void EmitSourceDolbyVisionWillBeStripped(
        EncodingProfile profile,
        MediaInfo source,
        List<EncoderRule> rules
    )
    {
        if (source.DolbyVision is null)
            return;
        if (profile.Video is not { Policy: StreamPolicy.Transcode })
            return;

        // Dolby Vision RPU survives only when the encoder + container combo preserves it. Without
        // a DV-capable path the RPU is stripped and the output becomes HDR10 (BL only).
        rules.Add(
            new(
                Id: EncoderRuleId.SourceDolbyVisionWillBeStripped,
                Severity: EncoderRuleSeverity.Warning,
                Field: "source.dolby_vision",
                Message: "Source contains Dolby Vision metadata (RPU). The encoder strips the RPU "
                    + "on re-encode; the output retains HDR10 base layer only.",
                Fix: "Set video.policy to Copy to preserve the Dolby Vision stream end-to-end."
            )
        );
    }

    private static void EmitStereoscopicSourceUnsupported(
        EncodingProfile profile,
        MediaInfo source,
        List<EncoderRule> rules
    )
    {
        if (source.StereoMode is null)
            return;
        if (profile.Video is not { Policy: StreamPolicy.Transcode })
            return;

        rules.Add(
            new(
                Id: EncoderRuleId.StereoscopicSourceUnsupported,
                Severity: EncoderRuleSeverity.Error,
                Field: "source.stereo_mode",
                Message: $"3D source detected (stereo_mode={source.StereoMode}). NoMercy does not "
                    + "support 3D re-encode; the stereo frame layout would be flattened.",
                Fix: "Switch video.policy to Copy to preserve the 3D stream."
            )
        );
    }

    private static void EmitSphericalMetadataWillBeStripped(
        EncodingProfile profile,
        MediaInfo source,
        List<EncoderRule> rules
    )
    {
        if (source.SphericalProjection is null)
            return;
        if (profile.Video is not { Policy: StreamPolicy.Transcode })
            return;

        rules.Add(
            new(
                Id: EncoderRuleId.SphericalMetadataWillBeStripped,
                Severity: EncoderRuleSeverity.Warning,
                Field: "source.spherical_projection",
                Message: $"VR projection metadata ({source.SphericalProjection}) will be stripped "
                    + "on re-encode; the output plays as a flat panoramic stretch.",
                Fix: "Use a Copy video policy to preserve the spherical metadata box."
            )
        );
    }

    private static void EmitSubtitlesBurnInPermanent(
        EncodingProfile profile,
        List<EncoderRule> rules
    )
    {
        // BurnIn writes subtitles into video pixels permanently. Worth flagging once so users
        // who pick the policy by accident see what they signed up for.
        foreach (
            SubtitleOutput subtitle in (profile.Subtitles ?? []).Where(s =>
                s.Policy == SubtitlePolicy.BurnIn
            )
        )
        {
            rules.Add(
                new(
                    Id: EncoderRuleId.SubtitlesBurnInPermanent,
                    Severity: EncoderRuleSeverity.Info,
                    Field: "subtitles.policy",
                    Message: $"Subtitle policy BurnIn writes {subtitle.Codec} into video pixels permanently; "
                        + "viewers cannot turn this off.",
                    Fix: "Switch subtitles.policy to Extract or Copy to keep the track toggleable."
                )
            );
            return;
        }
    }

    // ----------------------------------------------------------------------
    // Profile-only rules
    // ----------------------------------------------------------------------

    private static void EmitProfileLevelResolutionMismatch(
        EncodingProfile profile,
        List<EncoderRule> rules
    )
    {
        // Level vs resolution check at profile-save time: assume the spec target frame rate
        // (30 fps for SDR, 60 fps for HFR-marked profiles). Without a source we can only check
        // the static side: width × height × DefaultProfileFps vs the level's MaxLumaSamplesPerSec.
        // A null (or legacy 0) width means "keep source width" — this rule has
        // no source to derive it from, so it can't reason about the resolution
        // and must skip rather than false-flag.
        if (
            profile.Video is not { Policy: StreamPolicy.Transcode } video
            || string.IsNullOrEmpty(video.Level)
            || video.Width is not int width
            || width <= 0
        )
            return;

        int height = video.Height ?? width * 9 / 16; // fall back to 16:9
        const double assumedFps = 30.0;
        long lumaSamplesPerSec = (long)(width * height * assumedFps);

        CodecLevelFpsCaps.LevelCap? cap = CodecLevelFpsCaps.Lookup(video.Codec, video.Level);
        if (cap is null || lumaSamplesPerSec <= cap.MaxLumaSamplesPerSec)
            return;

        CodecLevelFpsCaps.LevelCap? nextFit = CodecLevelFpsCaps.FindNextFit(
            video.Codec,
            lumaSamplesPerSec
        );

        string fix = nextFit is not null
            ? $"Raise video.level to {nextFit.Level} (supports up to "
                + $"{nextFit.MaxLumaSamplesPerSec.ToString("N0", CultureInfo.InvariantCulture)} luma samples/sec)."
            : "No standard level supports this resolution at 30 fps.";

        rules.Add(
            new(
                Id: EncoderRuleId.LevelResolutionMismatch,
                Severity: EncoderRuleSeverity.Error,
                Field: "video.level",
                Message: $"Level {video.Level} cannot sustain {width}x{height} at 30 fps "
                    + $"({lumaSamplesPerSec.ToString("N0", CultureInfo.InvariantCulture)} luma samples/sec required, "
                    + $"level {video.Level} allows "
                    + $"{cap.MaxLumaSamplesPerSec.ToString("N0", CultureInfo.InvariantCulture)}).",
                Fix: fix
            )
        );
    }

    private static void EmitLevelInvalid(EncodingProfile profile, List<EncoderRule> rules)
    {
        // An explicit level must be one the codec actually defines. The
        // luma-cap rules above intentionally treat an unknown level as a
        // pass (they only compare against a known cap), so a bogus level like
        // H.264 "6.3" sailed through and reached ffmpeg as `-level 6.3`, which
        // libx264 rejects ("invalid level_idc"). Whitelist against the known
        // table — but only for codecs whose levels this catalogue enumerates,
        // so a codec with no table (e.g. AV1) is never false-flagged.
        if (
            profile.Video is not { Policy: StreamPolicy.Transcode } video
            || string.IsNullOrEmpty(video.Level)
            || !CodecLevelFpsCaps.HasLevelTable(video.Codec)
        )
            return;

        if (CodecLevelFpsCaps.Lookup(video.Codec, video.Level) is not null)
            return;

        rules.Add(
            new(
                Id: EncoderRuleId.LevelInvalid,
                Severity: EncoderRuleSeverity.Error,
                Field: "video.level",
                Message: $"Level '{video.Level}' is not a valid {video.Codec} level; "
                    + "ffmpeg would reject it and the encode would fail to start.",
                Fix: "Set video.level to a level the codec defines (e.g. \"4.0\", \"5.1\"), "
                    + "or leave it unset to let the encoder pick."
            )
        );
    }

    private static void EmitBitrateTooLowForResolution(
        EncodingProfile profile,
        List<EncoderRule> rules
    )
    {
        // Rough rule of thumb per resolution tier, in kbps. Source: SRGS encoding guidelines.
        // A null (or legacy 0) width means "keep source width" — this rule has
        // no source to derive it from, so it can't size the minimum bitrate.
        if (
            profile.Video is not { Policy: StreamPolicy.Transcode } video
            || (
                video.RateControl != RateControlMode.Vbr && video.RateControl != RateControlMode.Cbr
            )
            || video.BitrateKbps <= 0
            || video.Width is not int width
            || width <= 0
        )
            return;

        int minBitrate = MinimumBitrateKbpsFor(width);
        if (video.BitrateKbps >= minBitrate)
            return;

        rules.Add(
            new(
                Id: EncoderRuleId.BitrateTooLowForResolution,
                Severity: EncoderRuleSeverity.Warning,
                Field: "video.bitrate_kbps",
                Message: $"Bitrate {video.BitrateKbps} kbps is below the conservative minimum "
                    + $"({minBitrate} kbps) for {width}-wide output; visible artefacts likely.",
                Fix: $"Raise video.bitrate_kbps to at least {minBitrate}, "
                    + "or switch rate_control to CRF for quality-targeted encoding."
            )
        );
    }

    /// <summary>
    ///     Conservative minimum encoded bitrate for an output width, derived from the SRGS encoding
    ///     guideline ladder. Anything below the minimum is almost certainly artefact-heavy at the
    ///     declared resolution.
    /// </summary>
    public static int MinimumBitrateKbpsFor(int width)
    {
        if (width >= 3840)
            return 8_000;
        if (width >= 2560)
            return 5_000;
        if (width >= 1920)
            return 2_500;
        if (width >= 1280)
            return 1_500;
        if (width >= 854)
            return 700;
        return 300;
    }

    private static void EmitCrfOutOfTypicalRange(EncodingProfile profile, List<EncoderRule> rules)
    {
        // Typical x264/x265 CRF range is 17..28 for visually transparent / good quality. The codec
        // accepts 0..51 but values outside the typical range almost always mean a typo (e.g. 5
        // instead of 25) or a deliberate exotic choice. Surface as a warning either way.
        if (
            profile.Video is not { Policy: StreamPolicy.Transcode } video
            || video.RateControl != RateControlMode.Crf
            || (video.Codec != VideoCodecType.H264 && video.Codec != VideoCodecType.H265)
        )
            return;

        if (video.Crf is >= 17 and <= 28)
            return;

        rules.Add(
            new(
                Id: EncoderRuleId.CrfOutOfTypicalRange,
                Severity: EncoderRuleSeverity.Warning,
                Field: "video.crf",
                Message: $"CRF {video.Crf} is outside the typical 17..28 range for {video.Codec}; "
                    + (video.Crf < 17 ? "expect very large output." : "expect heavy artefacts."),
                Fix: "Set video.crf to 23 for transparent quality, "
                    + "20 for archival, or 26 for size-constrained delivery."
            )
        );
    }

    private static void EmitHlsKeyframeSegmentMisalignment(
        EncodingProfile profile,
        List<EncoderRule> rules
    )
    {
        // HLS segments must start on a keyframe. When the configured keyframe interval doesn't
        // divide evenly into the segment duration, the muxer either truncates segments early
        // (causing playback hiccups) or skips keyframes (hurting seek precision).
        if (
            profile.Container
            is not (
                Container.HlsTs
                or Container.HlsFmp4
                or Container.AudioHlsTs
                or Container.AudioHlsFmp4
            )
        )
            return;

        if (
            profile.Video is not { Policy: StreamPolicy.Transcode } video
            || video.KeyframeIntervalSeconds <= 0
            || profile.SegmentDurationSeconds <= 0
        )
            return;

        if (profile.SegmentDurationSeconds % video.KeyframeIntervalSeconds == 0)
            return;

        rules.Add(
            new(
                Id: EncoderRuleId.HlsKeyframeSegmentMisalignment,
                Severity: EncoderRuleSeverity.Warning,
                Field: "video.keyframe_interval_seconds",
                Message: $"Keyframe interval {video.KeyframeIntervalSeconds}s does not divide segment "
                    + $"duration {profile.SegmentDurationSeconds}s; segments may end mid-GOP and "
                    + "seek precision will suffer.",
                Fix: $"Set video.keyframe_interval_seconds to a divisor of {profile.SegmentDurationSeconds} "
                    + "(typically 2 for 6s segments)."
            )
        );
    }

    private static void EmitLadderInverted(EncodingProfile profile, List<EncoderRule> rules)
    {
        // Manual ladder inverted: a higher resolution rung has a lower bitrate than a lower-res
        // rung. ProfileValidator already rejects unsorted ascending bitrate; this rule catches the
        // resolution × bitrate inversion that the bitrate-only check misses.
        if (profile.Ladder is not { Mode: LadderMode.Manual, Rungs: { Length: > 1 } rungs })
            return;

        for (int i = 1; i < rungs.Length; i++)
        {
            LadderRung lower = rungs[i - 1];
            LadderRung higher = rungs[i];
            if (higher.Width <= lower.Width || higher.BitrateKbps >= lower.BitrateKbps)
                continue;

            rules.Add(
                new(
                    Id: EncoderRuleId.LadderInverted,
                    Severity: EncoderRuleSeverity.Error,
                    Field: $"ladder.rungs[{i}]",
                    Message: $"Ladder rung {i} ({higher.Width}x{higher.Height}, {higher.BitrateKbps} kbps) has "
                        + $"a wider resolution but a lower bitrate than rung {i - 1} "
                        + $"({lower.Width}x{lower.Height}, {lower.BitrateKbps} kbps).",
                    Fix: $"Raise rung {i} bitrate above rung {i - 1}, or reorder so wider resolutions come last."
                )
            );
            return;
        }
    }

    private static void EmitAudioAc3OffLadderBitrate(
        EncodingProfile profile,
        List<EncoderRule> rules
    )
    {
        // AC-3 / E-AC-3 only accept a fixed bitrate ladder per the AC-3 (ATSC A/52) spec. Any
        // bitrate outside this set silently picks the nearest supported value in libavcodec —
        // surprises the user when the encode output doesn't match the request.
        foreach (
            AudioOutput audio in (profile.Audio ?? []).Where(a =>
                a.Policy == StreamPolicy.Transcode
            )
        )
        {
            int[]? ladder = audio.Codec switch
            {
                AudioCodecType.Ac3 => Ac3BitrateLadderKbps,
                AudioCodecType.Eac3 => Eac3BitrateLadderKbps,
                _ => null,
            };
            if (ladder is null || audio.BitrateKbps <= 0)
                continue;
            if (Array.IndexOf(ladder, audio.BitrateKbps) >= 0)
                continue;

            int nearest = ladder.OrderBy(b => Math.Abs(b - audio.BitrateKbps)).First();
            rules.Add(
                new(
                    Id: audio.Codec == AudioCodecType.Ac3
                        ? EncoderRuleId.AudioAc3OffLadderBitrate
                        : EncoderRuleId.AudioEac3OffLadderBitrate,
                    Severity: EncoderRuleSeverity.Warning,
                    Field: "audio.bitrate_kbps",
                    Message: $"{audio.Codec} only supports a fixed bitrate ladder; {audio.BitrateKbps} kbps "
                        + "will be coerced by the encoder to the nearest supported value.",
                    Fix: $"Set audio.bitrate_kbps to {nearest} (nearest supported value)."
                )
            );
        }
    }

    /// <summary>ATSC A/52 AC-3 ladder, kbps. Any value outside the set is coerced by libavcodec.</summary>
    private static readonly int[] Ac3BitrateLadderKbps =
    [
        32,
        40,
        48,
        56,
        64,
        80,
        96,
        112,
        128,
        160,
        192,
        224,
        256,
        320,
        384,
        448,
        512,
        576,
        640,
    ];

    /// <summary>E-AC-3 ladder, kbps. Supports finer steps but still a fixed set.</summary>
    private static readonly int[] Eac3BitrateLadderKbps =
    [
        32,
        40,
        48,
        56,
        64,
        80,
        96,
        112,
        128,
        160,
        192,
        224,
        256,
        288,
        320,
        384,
        448,
        512,
        576,
        640,
        768,
        896,
        1024,
        1152,
        1280,
        1408,
        1536,
        1664,
        1792,
        1920,
        2048,
        2304,
        2560,
        2688,
        2816,
        2944,
        3072,
        3200,
        3328,
        3456,
        3584,
        3712,
        3840,
        3968,
        4096,
        4224,
        4352,
        4480,
        4608,
        4736,
        4864,
        4992,
        5120,
        5248,
        5376,
        5504,
        5632,
        5760,
        5888,
        6016,
        6144,
    ];

    private static void EmitSubtitlesContainerIncompatible(
        EncodingProfile profile,
        List<EncoderRule> rules
    )
    {
        // ContainerCompatibility owns the codec×container matrix; ask it whether each declared
        // subtitle codec round-trips through the chosen container without re-encoding.
        // Extract + Copy both write the subtitle track into the output. BurnIn renders into video
        // pixels — no track in the container — and Omit drops the track entirely. Validate only
        // the two policies that need container compatibility.
        foreach (
            SubtitleOutput subtitle in (profile.Subtitles ?? []).Where(s =>
                s.Policy is SubtitlePolicy.Extract or SubtitlePolicy.Copy
            )
        )
        {
            if (ContainerCompatibility.SupportsSubtitle(profile.Container, subtitle.Codec))
                continue;

            rules.Add(
                new(
                    Id: EncoderRuleId.SubtitlesContainerIncompatible,
                    Severity: EncoderRuleSeverity.Error,
                    Field: "subtitles.codec",
                    Message: $"Container {profile.Container} does not support subtitle codec "
                        + $"{subtitle.Codec}; the muxer will drop the track.",
                    Fix: $"Pick a container that supports {subtitle.Codec}, switch the subtitle "
                        + "codec to a compatible one, or change subtitles.policy to BurnIn."
                )
            );
        }
    }

    private static void EmitHdrInverseTonemapUnsupported(
        EncodingProfile profile,
        List<EncoderRule> rules
    )
    {
        // Reverse direction of HDR conversion: the encoder can tonemap HDR -> SDR, but never
        // synthesises HDR from an SDR source (inverse tonemap requires colour-volume metadata
        // the source doesn't have).
        if (profile.HdrPolicies != HdrPolicies.AlwaysPreserve)
            return;

        if (profile.Video is not { Policy: StreamPolicy.Transcode } video)
            return;

        if (video.BitDepth >= 10)
            return; // request is consistent — preserve HDR from a 10-bit source.

        rules.Add(
            new(
                Id: EncoderRuleId.HdrInverseTonemapUnsupported,
                Severity: EncoderRuleSeverity.Error,
                Field: "hdr_policy",
                Message: "HdrPolicy.AlwaysPreserve with an 8-bit video output asks the encoder to "
                    + "synthesise HDR from an SDR-shaped pipeline; no inverse-tonemap is provided.",
                Fix: "Either raise video.bit_depth to 10+ on an HDR-capable codec (H.265 / AV1 / VP9), "
                    + "or change hdr_policy to PassthroughWhenPossible / AlwaysTonemap."
            )
        );
    }

    private static void EmitBitDepthVp9ProfileMismatch(
        EncodingProfile profile,
        List<EncoderRule> rules
    )
    {
        // libvpx-vp9 profile numbering ties chroma subsampling AND bit-depth together:
        //   profile0 = 8-bit 4:2:0  | profile1 = 8-bit 4:2:2/4:4:4
        //   profile2 = 10/12-bit 4:2:0 | profile3 = 10/12-bit 4:2:2/4:4:4
        // In the NoMercy CodecProfile enum the H.264/H.265 naming is reused:
        //   Baseline / Main / High   → 8-bit profiles (profile0 / profile1)
        //   Main10  / High10         → 10-bit profiles (profile2 / profile3)
        // Setting e.g. Main10 with BitDepth=8 (or Main with BitDepth=10) is
        // structurally impossible — VP9 would silently ignore the mismatched flag.
        if (profile.Video is not { Policy: StreamPolicy.Transcode } video)
            return;
        if (video.Codec != VideoCodecType.Vp9)
            return;
        if (video.CodecProfile == CodecProfile.Auto)
            return; // Auto lets the encoder pick the profile — no conflict.

        bool profileImplies10Bit = video.CodecProfile is CodecProfile.Main10 or CodecProfile.High10;
        bool bitDepthIs10 = video.BitDepth >= 10;

        if (profileImplies10Bit == bitDepthIs10)
            return; // consistent — no rule.

        string impliedDepth = profileImplies10Bit ? "10-bit" : "8-bit";
        string requestedDepth = bitDepthIs10 ? "10-bit" : "8-bit";
        string fix = profileImplies10Bit
            ? $"Either raise video.bit_depth to 10 (to use a VP9 profile2/3 encoder), or change video.codec_profile to Main or High (8-bit VP9 profile0/1)."
            : $"Either set video.bit_depth to 8 (matching the 8-bit profile), or change video.codec_profile to Main10 or High10 (VP9 profile2/3 for 10-bit).";

        rules.Add(
            new(
                Id: EncoderRuleId.BitDepthVp9ProfileMismatch,
                Severity: EncoderRuleSeverity.Error,
                Field: "video.codec_profile",
                Message: $"VP9 codec_profile {video.CodecProfile} implies {impliedDepth} but "
                    + $"video.bit_depth is {video.BitDepth} ({requestedDepth}); "
                    + "libvpx-vp9 ties the profile number to both chroma subsampling and bit depth.",
                Fix: fix
            )
        );
    }

    private static void EmitBitDepthH26xProfilePromoted(
        EncodingProfile profile,
        List<EncoderRule> rules
    )
    {
        // H.264/H.265 tie the profile string to the bit depth: "baseline",
        // "main" and "high" are 8-bit-only. Emitting one next to a 10-bit pixel
        // format makes the encoder abort ("high profile doesn't support a bit
        // depth of 10"). The pipeline auto-promotes an 8-bit tier to its 10-bit
        // sibling (high -> high10, main -> main10) so the encode still succeeds,
        // but the operator asked for a tier that cannot carry 10-bit — surface a
        // warning so the dashboard shows what the profile string became.
        if (profile.Video is not { Policy: StreamPolicy.Transcode } video)
            return;
        if (video.Codec is not (VideoCodecType.H264 or VideoCodecType.H265))
            return;
        if (video.BitDepth < 10)
            return;

        bool profileIs8BitOnly =
            video.CodecProfile is CodecProfile.Baseline or CodecProfile.Main or CodecProfile.High;
        if (!profileIs8BitOnly)
            return;

        string promoted = video.CodecProfile == CodecProfile.High ? "High10" : "Main10";

        rules.Add(
            new(
                Id: EncoderRuleId.BitDepthH26xProfilePromoted,
                Severity: EncoderRuleSeverity.Warning,
                Field: "video.codec_profile",
                Message: $"{video.Codec} codec_profile {video.CodecProfile} is 8-bit only but "
                    + $"video.bit_depth is {video.BitDepth}; the profile will be promoted to "
                    + $"{promoted} so the encoder accepts the 10-bit pixel format.",
                Fix: $"Set video.codec_profile to {promoted} (10-bit) explicitly, or lower "
                    + "video.bit_depth to 8 to keep the 8-bit profile."
            )
        );
    }

    private static void EmitCustomArgsReservedFlag(EncodingProfile profile, List<EncoderRule> rules)
    {
        // The per-stream CustomArguments dicts are the real escape hatches the
        // pipeline merges into each output's flags (PlanStage merges
        // VideoOutput.CustomArguments last-wins). Validating only the top-level
        // profile.CustomArguments left every per-output override completely
        // unchecked — a video-level -rc / -maxrate / -profile:v silently clobbered
        // a resolver-owned flag with no error. Scan all four sources.
        ScanCustomArgs("custom_arguments", profile.CustomArguments, rules);

        if (profile.Video is not null)
            ScanCustomArgs("video.custom_arguments", profile.Video.CustomArguments, rules);

        AudioOutput[] audio = profile.Audio ?? [];
        for (int i = 0; i < audio.Length; i++)
            ScanCustomArgs($"audio[{i}].custom_arguments", audio[i].CustomArguments, rules);

        SubtitleOutput[] subtitles = profile.Subtitles ?? [];
        for (int i = 0; i < subtitles.Length; i++)
            ScanCustomArgs($"subtitles[{i}].custom_arguments", subtitles[i].CustomArguments, rules);
    }

    private static void ScanCustomArgs(
        string fieldPrefix,
        Dictionary<string, string>? customArgs,
        List<EncoderRule> rules
    )
    {
        if (customArgs is null || customArgs.Count == 0)
            return;

        foreach (string key in customArgs.Keys)
        {
            // Normalize: callers may store keys with or without the leading dash.
            string normalized = key.StartsWith('-') ? key : $"-{key}";
            if (!ReservedFlags.Contains(normalized))
                continue;

            rules.Add(
                new(
                    Id: EncoderRuleId.CustomArgsReservedFlag,
                    Severity: EncoderRuleSeverity.Error,
                    Field: $"{fieldPrefix}[{key}]",
                    Message: $"CustomArgument '{key}' overrides a flag the encoder derives from typed "
                        + "profile fields. The profile's declared values would be ignored and the "
                        + "validator can no longer guarantee the output matches the profile.",
                    Fix: $"Remove the '{key}' override and set the matching typed field "
                        + "(codec / container / preset / rate control / hardware preference / ladder) instead."
                )
            );
        }
    }

    // ----------------------------------------------------------------------
    // Source-dependent rules
    // ----------------------------------------------------------------------

    private static void EmitLevelFrameRateCapExceeded(
        EncodingProfile profile,
        MediaInfo source,
        List<EncoderRule> rules
    )
    {
        if (
            profile.Video is not { Policy: StreamPolicy.Transcode } video
            || string.IsNullOrEmpty(video.Level)
            || source.VideoStreams.Count == 0
        )
            return;

        VideoStreamInfo primaryVideo = source.VideoStreams[0];
        double fps = primaryVideo.AverageFrameRate ?? primaryVideo.FrameRate;
        if (fps <= 0)
            return;

        // A null (or legacy 0) width means "keep source width" — resolve it
        // against the actual source here, since the source is available.
        int effectiveWidth = video.Width is int w and > 0 ? w : primaryVideo.Width;
        int effectiveHeight = video.Height ?? primaryVideo.Height;
        long lumaSamplesPerSec = (long)(effectiveWidth * effectiveHeight * fps);

        CodecLevelFpsCaps.LevelCap? cap = CodecLevelFpsCaps.Lookup(video.Codec, video.Level);
        if (cap is null || lumaSamplesPerSec <= cap.MaxLumaSamplesPerSec)
            return;

        CodecLevelFpsCaps.LevelCap? nextFit = CodecLevelFpsCaps.FindNextFit(
            video.Codec,
            lumaSamplesPerSec
        );

        string fix = nextFit is not null
            ? $"Raise video.level to {nextFit.Level} (supports up to "
                + $"{nextFit.MaxLumaSamplesPerSec.ToString("N0", CultureInfo.InvariantCulture)} luma samples/sec)."
            : "No standard level supports this resolution × frame rate.";

        rules.Add(
            new(
                Id: EncoderRuleId.LevelFrameRateCapExceeded,
                Severity: EncoderRuleSeverity.Error,
                Field: "video.level",
                Message: $"Source {fps.ToString("F2", CultureInfo.InvariantCulture)} fps × "
                    + $"{effectiveWidth}x{effectiveHeight} requires "
                    + $"{lumaSamplesPerSec.ToString("N0", CultureInfo.InvariantCulture)} luma samples/sec; level "
                    + $"{video.Level} allows "
                    + $"{cap.MaxLumaSamplesPerSec.ToString("N0", CultureInfo.InvariantCulture)}.",
                Fix: fix
            )
        );
    }
}
