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

namespace NoMercy.Encoder.Distribution;

/// <summary>
/// Probe for hosts without a media analyzer (test bootstraps): never
/// measures anything, so a task without a declared duration reports no
/// percentage.
/// </summary>
public sealed class NullSourceDurationProbe : ISourceDurationProbe
{
    public Task<TimeSpan?> ProbeAsync(string inputPath, CancellationToken ct) =>
        Task.FromResult<TimeSpan?>(null);
}
