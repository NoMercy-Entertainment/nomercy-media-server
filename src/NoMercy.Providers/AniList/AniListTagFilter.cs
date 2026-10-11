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

using NoMercy.Providers.AniList.Models;

namespace NoMercy.Providers.AniList;

public static class AniListTagFilter
{
    // AniList also carries Cast-*, Technical and Sexual Content tags that are not
    // themes, and adult tags are always excluded. Shared by enrichment and the
    // theme seed so both agree on what a theme is.
    public static bool IsTheme(AniListMediaTag tag)
    {
        return !string.IsNullOrWhiteSpace(tag.Name)
            && !tag.IsAdult
            && tag.Category is not null
            && (tag.Category.StartsWith("Theme") || tag.Category.StartsWith("Setting"));
    }
}
