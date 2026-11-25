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
/// Outcome of one <see cref="SqliteCompactor"/> pass.
/// </summary>
/// <param name="Compacted">Free pages were reclaimed and the file on disk shrank.</param>
/// <param name="FreeBytesBefore">Bytes held by free pages before the pass.</param>
/// <param name="BytesBefore">Size on disk (file plus WAL) before the pass.</param>
/// <param name="BytesAfter">Size on disk (file plus WAL) after the pass.</param>
/// <param name="CheckpointBlocked">A reader older than the rebuild kept the file from shrinking.</param>
public sealed record SqliteCompactionResult(
    bool Compacted,
    long FreeBytesBefore,
    long BytesBefore,
    long BytesAfter,
    bool CheckpointBlocked = false
)
{
    /// <summary>Bytes the file system got back; never negative, 0 while the file is held.</summary>
    public long FreedBytes => Math.Max(0, BytesBefore - BytesAfter);
}
