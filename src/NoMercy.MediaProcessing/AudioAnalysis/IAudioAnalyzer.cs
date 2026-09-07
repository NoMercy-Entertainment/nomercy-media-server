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

namespace NoMercy.MediaProcessing.AudioAnalysis;

public interface IAudioAnalyzer
{
    /// <summary>
    /// The analyzer that produced a result. Persisted per row so improving the
    /// analyzer re-queues only the rows it invalidates, rather than forcing a
    /// full library rescan.
    /// </summary>
    int Version { get; }

    /// <summary>
    /// Measures one track. Returns null when the file yielded nothing usable,
    /// which the caller records as a terminal state rather than retrying.
    /// </summary>
    Task<AudioAnalysisResult?> AnalyzeAsync(string filePath, CancellationToken ct);
}
