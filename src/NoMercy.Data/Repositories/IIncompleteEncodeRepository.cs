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

using NoMercy.Database.Models.Encoder;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;

namespace NoMercy.Data.Repositories;

/// <summary>
/// The dashboard's quarantine list for encodes that could not finish, and the
/// media-side lookups needed to re-dispatch one.
/// </summary>
public interface IIncompleteEncodeRepository
{
    Task<List<IncompleteEncode>> GetAllAsync();

    Task<IncompleteEncode?> FindAsync(int id);

    Task RemoveAsync(IncompleteEncode row);

    Task<int> RemoveAllAsync();

    /// <summary>The library that owns the folder a quarantined encode lives in.</summary>
    Task<FolderLibrary?> FindFolderLibraryAsync(Ulid folderId);

    /// <summary>The best available video file for a quarantined movie or episode.</summary>
    Task<VideoFile?> FindVideoFileForMediaAsync(int mediaId);
}
