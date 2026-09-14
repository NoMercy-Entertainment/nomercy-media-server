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

namespace NoMercy.Data.Repositories;

/// <summary>
/// Aggregate counts for the dashboard's audio-analysis status panel. Backed by
/// its own <c>MediaContext</c> rather than a request-scoped one — this is read
/// alongside a queue-side count on the same poll, and sharing the scoped
/// context with that concurrent read fails intermittently under load.
/// </summary>
public interface IAudioAnalysisStatisticsRepository
{
    Task<AudioAnalysisCounts> GetCountsAsync(CancellationToken cancellationToken = default);
}
