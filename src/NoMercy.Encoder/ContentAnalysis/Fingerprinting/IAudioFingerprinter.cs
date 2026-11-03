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

using NoMercy.Storage;

namespace NoMercy.Encoder.ContentAnalysis.Fingerprinting;

/// <summary>
/// Extracts a chromaprint audio fingerprint from a file. Windowed — the
/// intro detector only needs the first few minutes, and the outro detector
/// only the last few. Fingerprinting the whole file would be wasteful.
/// </summary>
public interface IAudioFingerprinter
{
    /// <summary>
    /// Produce a fingerprint for the given window. Pass <c>null</c> for
    /// <paramref name="window"/> to fingerprint the entire file (useful
    /// for short snippets; avoid for full feature-length content).
    /// </summary>
    /// <param name="sourceStorage">
    /// The storage <paramref name="filePath"/> is addressed against — the
    /// library folder's own storage for a scope-relative VideoFile path.
    /// Null (the default) keeps the fingerprinter's injected storage, for
    /// callers that already hold a path that storage resolves.
    /// </param>
    Task<AudioFingerprint> FingerprintAsync(
        string filePath,
        FingerprintWindow? window,
        CancellationToken ct,
        IStorage? sourceStorage = null
    );
}
