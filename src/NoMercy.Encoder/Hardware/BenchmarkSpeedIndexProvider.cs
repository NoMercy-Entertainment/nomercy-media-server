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

namespace NoMercy.Encoder.Hardware;

/// <summary>
/// <see cref="ISpeedIndexProvider"/> backed by the hardware benchmark's
/// cache, so consumers see new measurements without a server restart.
/// </summary>
public class BenchmarkSpeedIndexProvider(IHardwareBenchmark benchmark) : ISpeedIndexProvider
{
    public SpeedIndex Current => benchmark.GetCachedIndex();
}
