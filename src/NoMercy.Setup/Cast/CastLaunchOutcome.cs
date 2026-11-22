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

namespace NoMercy.Setup.Cast;

/// <summary>What a server-side Cast launch really did, for logging; no caller branches on it.</summary>
public enum CastLaunchOutcome
{
    Launched,
    SkippedLive,
    NoAddress,
    NoReceiver,
    Failed,
}
