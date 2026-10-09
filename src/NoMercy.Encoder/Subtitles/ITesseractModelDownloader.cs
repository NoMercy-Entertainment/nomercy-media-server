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

namespace NoMercy.Encoder.Subtitles;

/// <summary>
/// Downloads a single Tesseract <c>{language}.traineddata</c> asset from the signed
/// NoMercy-Entertainment/nomercy-tesseract release, verifying the release manifest's
/// PGP signature and the asset's SHA-256 before returning its bytes.
/// </summary>
/// <remarks>
/// Implemented in NoMercy.Setup, which owns release/manifest signature verification —
/// NoMercy.Encoder cannot reference it directly (NoMercy.Setup already references
/// NoMercy.Encoder), so <see cref="TesseractModelManager"/> depends on this abstraction
/// and the host wires the concrete implementation through DI.
/// </remarks>
public interface ITesseractModelDownloader
{
    /// <summary>
    /// Lists model languages published as assets in the latest signature-verified manifest.
    /// </summary>
    Task<IReadOnlyList<string>> GetAvailableLanguagesAsync(CancellationToken ct);

    /// <summary>
    /// Downloads and verifies the <c>{language}.traineddata</c> asset from the latest
    /// signed nomercy-tesseract release.
    /// </summary>
    /// <returns>A stream positioned at the start of the verified model bytes.</returns>
    /// <exception cref="InvalidOperationException">
    /// The release or its manifest could not be resolved, the manifest signature did not
    /// verify, or no signed asset exists for <paramref name="language"/>. No unverified
    /// fallback is ever attempted.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The downloaded bytes do not match the SHA-256 recorded in the signed manifest.
    /// </exception>
    Task<Stream> DownloadVerifiedAsync(string language, CancellationToken ct);
}
