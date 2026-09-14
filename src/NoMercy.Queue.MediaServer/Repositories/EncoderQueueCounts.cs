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

namespace NoMercy.Queue.MediaServer.Repositories;

/// <summary>
/// How much encoder work of each kind is waiting and how much is in flight,
/// counted over the whole queue rather than over a bounded listing sample.
/// </summary>
public sealed record EncoderQueueCounts(int VideoPending, int MaintenancePending, int RunningTotal);
