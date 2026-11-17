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

using NoMercy.Database.Models.Media;

namespace NoMercy.Database.Segments;

/// <summary>
/// What one writer changes for one episode or movie after
/// <see cref="ContentSegmentPrecedence.Plan"/> applied the precedence order:
/// the stored rows to delete and the new rows to insert.
/// </summary>
public sealed record ContentSegmentWritePlan(
    IReadOnlyList<ContentSegment> Remove,
    IReadOnlyList<ContentSegment> Add
)
{
    public bool IsEmpty => Remove.Count == 0 && Add.Count == 0;
}
