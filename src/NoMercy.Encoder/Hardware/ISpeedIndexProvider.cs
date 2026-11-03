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
/// Hands out the speed index the hardware benchmark holds right now. Read
/// <see cref="Current"/> at the moment of use and do not keep the result:
/// the benchmark replaces its index after every probe and after a full run,
/// so a stored copy goes stale (issue #350).
/// </summary>
public interface ISpeedIndexProvider
{
    SpeedIndex Current { get; }
}
