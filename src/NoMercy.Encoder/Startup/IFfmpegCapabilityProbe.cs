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

using NoMercy.Encoder.Errors;

namespace NoMercy.Encoder.Startup;

/// <summary>
/// Probes for optional runtime dependencies (fpcalc, whisper.cpp model,
/// stemsplit GGUF model, Tesseract traineddata) and validates that the
/// installed FFmpeg build
/// contains the filters, protocols, and muxers the server relies on.
/// Results are cached after the first probe and returned via
/// <see cref="GetCachedReport"/>. The probe itself runs deferred — never
/// during startup or during user-visible work.
/// </summary>
public interface IFfmpegCapabilityProbe
{
    /// <summary>Executes all probes and caches the result.</summary>
    Task ProbeAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the cached report, or <c>null</c> if <see cref="ProbeAsync"/>
    /// has not yet completed.
    /// </summary>
    CapabilityReport? GetCachedReport();
}

/// <summary>
/// Immutable snapshot produced by <see cref="IFfmpegCapabilityProbe"/>.
/// </summary>
public sealed record CapabilityReport(
    bool BluRayProtocol,
    bool DvdReadProtocol,
    IReadOnlyList<string> AvailableEncoders,
    IReadOnlyList<string> MissingFilters,
    IReadOnlyList<string> MissingMuxers,
    bool FpcalcPresent,
    bool WhisperModelPresent,
    bool StemsplitModelPresent,
    bool TesseractEngTraineddataPresent,
    string? TesseractModelsDirectory,
    IReadOnlyList<EncoderRule> Issues
);
