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

/// <summary>
/// Puts every track in the named libraries that still lacks a verdict from the
/// current analyzer onto the music queue.
/// <para>
/// The hourly sweep, the hook that fires when a library scan finishes and the
/// dashboard's run-now button all ask the same question, so they all ask it
/// here. The contract lives beside the query it runs rather than beside the
/// implementation, because the API layer calls it and never references the
/// host.
/// </para>
/// </summary>
public interface IAudioAnalysisScheduler
{
    /// <summary>
    /// Queues the outstanding tracks of the given libraries and answers how
    /// many jobs were dispatched. An empty set queues nothing.
    /// </summary>
    Task<int> QueueAsync(
        IReadOnlyCollection<Ulid> libraryIds,
        CancellationToken cancellationToken = default
    );
}
