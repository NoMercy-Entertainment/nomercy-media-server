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

namespace NoMercy.Encoder.Errors;

/// <summary>
/// Stable, dashboard-deep-linkable identifiers for every rule the
/// encoder can emit. Catalogued centrally so the docs site, dashboard
/// chip renderer, and validators all reference the same string.
///
/// Phase 1.2 of <c>encoder-v3-alignment.md</c> back-fills the validator
/// to emit these IDs; Phase 4.19 closes the loop by asserting every
/// rule ID has a matching docs entry.
/// </summary>
public static class EncoderRuleId
{
    // ---- Profile structural ----------------------------------------------
    public const string ProfileNameMissing = "profile.name_missing";
    public const string ProfileNoOutputs = "profile.no_outputs";
    public const string ProfileBuiltinReadonly = "profile.builtin_readonly";

    // ---- Video output ----------------------------------------------------
    public const string VideoWidthInvalid = "video.width_invalid";
    public const string VideoHeightInvalid = "video.height_invalid";
    public const string VideoRateControlMissing = "video.rate_control_missing";
    public const string VideoRateControlConflict = "video.rate_control_conflict";

    // ---- Codec / level / container ---------------------------------------
    public const string LevelResolutionMismatch = "level.resolution_mismatch";
    public const string LevelInvalid = "level.invalid";
    public const string CodecContainerMismatch = "codec.container_mismatch";

    // ---- Audio -----------------------------------------------------------
    public const string AudioCodecContainerMismatch = "audio.codec_container_mismatch";
    public const string AudioAc3OffLadderBitrate = "audio.ac3_off_ladder_bitrate";
    public const string AudioEac3OffLadderBitrate = "audio.eac3_off_ladder_bitrate";
    public const string AudioBitrateMissing = "audio.bitrate_missing";
    public const string LadderManualEmpty = "ladder.manual_empty";
    public const string LadderManualUnsorted = "ladder.manual_unsorted";

    // ---- Bitrate / CRF ---------------------------------------------------
    public const string BitrateTooLowForResolution = "bitrate.too_low_for_resolution";
    public const string CrfOutOfTypicalRange = "crf.out_of_typical_range";

    // ---- Bit depth -------------------------------------------------------
    public const string BitDepthNoHardwareSupport = "bit_depth.no_hardware_support";
    public const string BitDepthVp9ProfileMismatch = "bit_depth.vp9_profile_mismatch";
    public const string BitDepthH26xProfilePromoted = "bit_depth.h26x_profile_promoted";
    public const string BitDepthAutoDowngrade = "bit_depth.auto_downgrade";
    public const string BitDepthStrictViolation = "bit_depth.strict_violation";

    // ---- Ladder ----------------------------------------------------------
    public const string LadderInverted = "ladder.inverted";
    public const string LadderDuplicateVariant = "ladder.duplicate_variant";

    // ---- HLS / segments --------------------------------------------------
    public const string HlsKeyframeSegmentMisalignment = "hls.keyframe_segment_misalignment";
    public const string HlsFmp4CodecMismatch = "hls.fmp4_codec_mismatch";

    // ---- Custom ffmpeg args ---------------------------------------------
    public const string CustomArgsReservedFlag = "custom_args.reserved_flag";

    // ---- Subtitles -------------------------------------------------------
    public const string SubtitlesContainerIncompatible = "subtitles.container_incompatible";
    public const string SubtitlesBurnInPermanent = "subtitles.burn_in_permanent";
    public const string SubtitlesAssNeedsCapableClient = "subtitles.ass_needs_capable_client";

    // ---- HDR -------------------------------------------------------------
    public const string HdrInverseTonemapUnsupported = "hdr.inverse_tonemap_unsupported";

    // ---- Source warnings (preview) ---------------------------------------
    public const string SourceVariableFrameRate = "source.variable_frame_rate";
    public const string SourceDolbyVisionWillBeStripped = "source.dolby_vision_will_be_stripped";
    public const string SourceUpscalingDetected = "source.upscaling_detected";
    public const string LevelFrameRateCapExceeded = "level.frame_rate_cap_exceeded";
    public const string StereoscopicSourceUnsupported = "source.stereoscopic_unsupported";
    public const string SphericalMetadataWillBeStripped =
        "source.spherical_metadata_will_be_stripped";

    // ---- Inheritance -----------------------------------------------------
    public const string ParentIdCycle = "parent_id.cycle";

    // ---- DRM / signing ---------------------------------------------------
    public const string DrmKeyMissing = "drm.key_missing";
    public const string DrmHttpNotHttps = "drm.http_not_https";
    public const string ImportHttpNotHttps = "import.http_not_https";
    public const string ImportFetchFailed = "import.fetch_failed";
    public const string ImportSourceMissing = "import.source_missing";
    public const string ImportJsonMalformed = "import.json_malformed";
    public const string ImportSignatureInvalid = "import.signature_invalid";
    public const string ImportPublisherUntrusted = "import.publisher_untrusted";
    public const string ImportUnsignedRequiresFlag = "import.unsigned_requires_flag";

    // ---- Trusted publisher keys ------------------------------------------
    public const string TrustedPublisherPublicKeyInvalid = "trusted_publisher.public_key_invalid";
    public const string TrustedPublisherAlreadyTrusted = "trusted_publisher.already_trusted";

    // ---- Hardware --------------------------------------------------------
    public const string HardwareForcedButUnavailable = "hardware.forced_but_unavailable";
    public const string HardwareGpuTelemetryUnsupported = "hardware.gpu_telemetry_unsupported";

    // ---- Capabilities ----------------------------------------------------
    public const string CapabilityFpcalcMissing = "capability.fpcalc_missing";
    public const string CapabilityWhisperMissing = "capability.whisper_missing";
    public const string CapabilityStemsplitMissing = "capability.stemsplit_missing";
    public const string CapabilityTesseractModelMissing = "capability.tesseract_model_missing";

    // ---- Disc ------------------------------------------------------------
    public const string DiscDriveBusy = "disc.drive_busy";
    public const string DiscAacsCertMissing = "disc.aacs_cert_missing";
    public const string DiscBdplusConverterMissing = "disc.bdplus_converter_missing";
    public const string DiscReadError = "disc.read_error";

    // ---- Output / job ----------------------------------------------------
    public const string OutputPathNotAllowed = "output.path_not_allowed";
    public const string OutputWriteError = "output.write_error";
    public const string SourceNotAccessible = "source.not_accessible";
    public const string SourceReadError = "source.read_error";
    public const string JobInterruptedNoCheckpoint = "job.interrupted_no_checkpoint";

    // ---- Encoder runtime -------------------------------------------------
    public const string EncoderInitFailed = "encoder.init_failed";
    public const string GpuCapacityExhausted = "gpu_capacity_exhausted";

    // ---- License ---------------------------------------------------------
    public const string LicenseRevoked = "license.revoked";
    public const string LicenseUnreachable = "license.unreachable";

    // ---- Distribution ----------------------------------------------------
    public const string DistributionHmacInvalid = "distribution.hmac_invalid";
    public const string DistributionTimestampReplay = "distribution.timestamp_replay";
    public const string DistributionWorkerNotRegistered = "distribution.worker_not_registered";
}
