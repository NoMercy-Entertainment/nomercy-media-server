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

namespace NoMercy.Encoder.ContentAnalysis.Segments;

/// <summary>
/// A shared run in hash indexes: hashes <c>[AStart, AEnd)</c> of the first print
/// match hashes <c>[BStart, BEnd)</c> of the second.
/// </summary>
internal readonly record struct HashRun(int AStart, int AEnd, int BStart, int BEnd)
{
    public int Length => AEnd - AStart;
}
