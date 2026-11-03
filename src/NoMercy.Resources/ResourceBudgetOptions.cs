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

namespace NoMercy.Resources;

/// <summary>
/// Live-headroom thresholds consulted by <see cref="IResourceBudget.TryAcquire"/>
/// before granting a new encoder lease. The static semaphores still enforce
/// hardware ceilings (NVENC session caps, CPU thread count) — these
/// thresholds are the dynamic throttle on top, so a 16-core box with 8
/// CPU-thread budget doesn't actually launch 8 encodes when 4 already pin
/// the host.
///
/// Any threshold left at or below 0 (or <c>MinFreeMemoryMb</c> at 0) is
/// treated as disabled. Default values err on the side of "leave headroom
/// for the user's other work" — they don't max out the box.
///
/// <c>GpuEncodePercentPerLease</c> is the estimated encoder load of one
/// running GPU encode. The GPU gate takes the higher of the measured load
/// and running GPU slots × this estimate, so concurrent GPU encodes still
/// count where nothing measures the encoder (non-NVIDIA hosts, the first
/// seconds after boot). 20 % lets about five encodes run under a 95 %
/// headroom; 0 turns the estimate off.
/// </summary>
public sealed record ResourceBudgetOptions(
    double CpuHeadroomPercent = 75.0,
    double GpuHeadroomPercent = 80.0,
    long MinFreeMemoryMb = 1024,
    double GpuEncodePercentPerLease = 20.0
)
{
    /// <summary>
    /// Permissive defaults that disable every live-headroom check. Used by
    /// the legacy ResourceBudget constructor + tests that don't want to
    /// model live host load.
    /// </summary>
    public static ResourceBudgetOptions Disabled { get; } =
        new(
            CpuHeadroomPercent: 0,
            GpuHeadroomPercent: 0,
            MinFreeMemoryMb: 0,
            GpuEncodePercentPerLease: 0
        );
}
