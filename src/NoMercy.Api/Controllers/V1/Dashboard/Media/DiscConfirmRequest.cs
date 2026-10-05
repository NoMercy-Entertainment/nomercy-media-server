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

public record DiscConfirmRequest(
    string TmdbId,
    /// <summary>"movie" or "tv"</summary>
    string MediaType,
    string RipOutputPath,
    Ulid LibraryId,
    Ulid FolderId,
    string? Title = null,
    int? Year = null,
    string? PosterUrl = null,
    int? SeasonNumber = null,
    int? EpisodeNumber = null
);
