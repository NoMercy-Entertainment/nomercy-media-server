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
/// Stable, machine-readable identifiers for every runtime error the encoder
/// pipeline can emit during an active job. These IDs appear in HTTP error
/// responses, SignalR notifications, and the docs site so clients and
/// operators can deep-link straight to the relevant guidance.
///
/// <para>All constants are thin aliases to <see cref="EncoderRuleId"/>,
/// which is the single source of truth. Callers that need only runtime
/// error IDs (controllers, middleware) can import this class without
/// pulling in the full rule-validator catalogue.</para>
///
/// <para>Phase 4.19 of <c>encoder-v3-alignment.md</c> establishes this
/// class; <see cref="RuntimeErrors"/> factories reference
/// <see cref="EncoderRuleId"/> directly so both classes stay in sync.</para>
/// </summary>
public static class EncoderRuntimeErrorId
{
    // ---- Hardware ------------------------------------------------------------
    public const string HardwareForcedButUnavailable = EncoderRuleId.HardwareForcedButUnavailable;

    public const string HardwareGpuTelemetryUnsupported =
        EncoderRuleId.HardwareGpuTelemetryUnsupported;

    public const string GpuCapacityExhausted = EncoderRuleId.GpuCapacityExhausted;

    // ---- Encoder runtime -----------------------------------------------------
    public const string EncoderInitFailed = EncoderRuleId.EncoderInitFailed;

    // ---- Source --------------------------------------------------------------
    public const string SourceNotAccessible = EncoderRuleId.SourceNotAccessible;

    public const string SourceReadError = EncoderRuleId.SourceReadError;

    // ---- Output --------------------------------------------------------------
    public const string OutputWriteError = EncoderRuleId.OutputWriteError;

    public const string OutputPathNotAllowed = EncoderRuleId.OutputPathNotAllowed;

    // ---- License -------------------------------------------------------------
    public const string LicenseRevoked = EncoderRuleId.LicenseRevoked;

    public const string LicenseUnreachable = EncoderRuleId.LicenseUnreachable;

    // ---- Capabilities --------------------------------------------------------
    public const string CapabilityFpcalcMissing = EncoderRuleId.CapabilityFpcalcMissing;

    public const string CapabilityWhisperMissing = EncoderRuleId.CapabilityWhisperMissing;

    public const string CapabilityStemsplitMissing = EncoderRuleId.CapabilityStemsplitMissing;

    public const string CapabilityTesseractModelMissing =
        EncoderRuleId.CapabilityTesseractModelMissing;

    // ---- Disc ----------------------------------------------------------------
    public const string DiscDriveBusy = EncoderRuleId.DiscDriveBusy;

    public const string DiscAacsCertMissing = EncoderRuleId.DiscAacsCertMissing;

    public const string DiscBdplusConverterMissing = EncoderRuleId.DiscBdplusConverterMissing;

    public const string DiscReadError = EncoderRuleId.DiscReadError;

    // ---- Job -----------------------------------------------------------------
    public const string JobInterruptedNoCheckpoint = EncoderRuleId.JobInterruptedNoCheckpoint;

    // ---- Distribution --------------------------------------------------------
    public const string DistributionHmacInvalid = EncoderRuleId.DistributionHmacInvalid;

    public const string DistributionTimestampReplay = EncoderRuleId.DistributionTimestampReplay;

    public const string DistributionWorkerNotRegistered =
        EncoderRuleId.DistributionWorkerNotRegistered;
}
