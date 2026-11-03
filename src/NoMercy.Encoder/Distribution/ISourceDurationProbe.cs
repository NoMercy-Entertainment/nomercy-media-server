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
/// Measures the full duration of a task's source file so progress can be
/// reported against it when the task carries neither a time range nor a
/// <see cref="EncodeTask.SourceDuration"/>. Implementations must never
/// throw for a source that cannot be measured: they return null and the
/// encode still runs, only without a percentage.
/// </summary>
public interface ISourceDurationProbe
{
    Task<TimeSpan?> ProbeAsync(string inputPath, CancellationToken ct);
}
