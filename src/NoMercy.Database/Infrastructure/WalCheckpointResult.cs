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

namespace NoMercy.Database;

/// <summary>
/// Outcome of one <see cref="SqliteWalCheckpointer"/> pass.
/// </summary>
/// <param name="WalFrames">Frames in the WAL before the pass; -1 when the database is not in WAL mode.</param>
/// <param name="OverLimit">The WAL was larger than the limit.</param>
/// <param name="Truncated">The WAL was restarted and its file emptied.</param>
public sealed record WalCheckpointResult(long WalFrames, bool OverLimit, bool Truncated);
