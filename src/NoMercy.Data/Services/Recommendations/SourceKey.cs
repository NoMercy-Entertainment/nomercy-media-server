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

namespace NoMercy.Data.Services.Recommendations;

/// <summary>
/// Identifies a title the user owns that a recommendation came from. Movies and
/// shows draw their ids from different TMDB id spaces, so the id alone is ambiguous:
/// a movie and a show can share a number without being related.
/// </summary>
/// <param name="MediaType">One of the <see cref="NoMercy.NmSystem.Domain.MediaTypes"/> constants.</param>
/// <param name="ItemId">The TMDB id of the title within that media type.</param>
public readonly record struct SourceKey(string MediaType, int ItemId);
