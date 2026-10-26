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

namespace NoMercy.Data.Visualizers;

public sealed record VisualizerListQuery(
    Guid UserId,
    bool IncludeDisabled,
    IReadOnlyList<string> Formats,
    string? Tag,
    string? Author,
    string? Search,
    bool FavoritesOnly,
    string Sort,
    int Page,
    int Take
)
{
    public const string SortName = "name";
    public const string SortAuthor = "author";
    public const string SortRecent = "recent";
}
