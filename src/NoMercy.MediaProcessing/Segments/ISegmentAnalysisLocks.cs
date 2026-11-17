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

namespace NoMercy.MediaProcessing.Segments;

/// <summary>
/// In-process locks so two analysis runs for one season or movie never
/// overlap (the queue can hold a scheduled and an on-demand job for it at
/// once), and analysis writes never interleave their read-plan-apply steps.
/// </summary>
public interface ISegmentAnalysisLocks
{
    /// <summary>Waits until <paramref name="key"/> is free and holds it until disposed.</summary>
    Task<IAsyncDisposable> AcquireAsync(string key, CancellationToken ct);
}
