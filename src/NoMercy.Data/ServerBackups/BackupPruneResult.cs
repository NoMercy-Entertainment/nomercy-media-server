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

namespace NoMercy.Data.ServerBackups;

/// <summary>
/// What a prune removed. <see cref="SetBytes"/> includes the half-built sets a
/// crashed backup left; <see cref="CopiesRemoved"/> counts single-database copies made by the
/// server; <see cref="HandMadeRemoved"/> counts the copies a person made by hand.
/// </summary>
public sealed record BackupPruneResult(
    int SetsRemoved,
    long SetBytes,
    int CopiesRemoved,
    long CopyBytes,
    int HandMadeRemoved = 0,
    long HandMadeBytes = 0
);
