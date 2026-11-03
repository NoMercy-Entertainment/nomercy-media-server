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

using NoMercy.Encoder.Hardware;

namespace NoMercy.Tests.Encoder.Hardware;

/// <summary>
/// Test double for <see cref="ISpeedIndexProvider"/>. Set <see cref="Current"/>
/// to simulate the benchmark publishing a new index.
/// </summary>
public sealed class StubSpeedIndexProvider : ISpeedIndexProvider
{
    public StubSpeedIndexProvider()
        : this(new SpeedIndex(new())) { }

    public StubSpeedIndexProvider(SpeedIndex current)
    {
        Current = current;
    }

    public SpeedIndex Current { get; set; }
}
