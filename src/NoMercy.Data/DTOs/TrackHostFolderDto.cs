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

namespace NoMercy.Data.DTOs;

/// <summary>
/// One music track's host folder, owning album and library folder, as tracked for a
/// library. Used to find every folder that needs a re-run of the track matcher, and
/// the library folder that re-run stores under.
/// </summary>
public record TrackHostFolderDto(string HostFolder, Guid AlbumId, Ulid FolderId);
