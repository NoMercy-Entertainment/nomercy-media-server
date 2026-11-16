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

namespace NoMercy.Service.Hosting;

/// <summary>
/// The outcome of taking the instance lock: the lock, or why not.
/// <paramref name="HeldElsewhere"/> is true only when another process holds the
/// file; any other failure leaves it false, with <paramref name="Problem"/> saying
/// what went wrong.
/// </summary>
public sealed record ServerInstanceLockAttempt(
    ServerInstanceLock? Lock,
    bool HeldElsewhere,
    string? Problem
);
