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

namespace NoMercy.Api.Controllers.V1.Dashboard.Media;

public record DiscOnboardingConfirmRequest(
    string Source,
    string StableId,
    string Title,
    string MediaType,
    int[] SelectedTitleIndices,
    Ulid LibraryId,
    Ulid FolderId,
    int? Year = null,
    string? PosterUrl = null,
    int? SeasonNumber = null,
    int? EpisodeNumber = null
);
