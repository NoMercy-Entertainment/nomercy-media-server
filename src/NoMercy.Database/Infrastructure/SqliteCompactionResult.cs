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
/// <param name="Compacted">Free pages were reclaimed.</param>
/// <param name="FreeBytesBefore">Bytes held by free pages before the pass.</param>
/// <param name="BytesBefore">Database size in bytes before the pass.</param>
/// <param name="BytesAfter">Database size in bytes after the pass.</param>
public sealed record SqliteCompactionResult(
    bool Compacted,
    long FreeBytesBefore,
    long BytesBefore,
    long BytesAfter
);
