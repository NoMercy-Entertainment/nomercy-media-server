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

public interface IAniListMetadataProvider
{
    Task<AniListMedia?> SearchAsync(string title, int? year, bool? priority = false);

    Task<AniListMediaTag[]?> GetTagCollectionAsync(bool? priority = false);
}
