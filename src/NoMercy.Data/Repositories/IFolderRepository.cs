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

using NoMercy.Data.DTOs;
using NoMercy.Database.Models.Libraries;

namespace NoMercy.Data.Repositories;

public interface IFolderRepository
{
    Task<Folder?> GetFolderByIdAsync(Ulid folderId);

    Task<Folder?> GetFolderByPathAsync(string requestPath);

    Task<Folder?> GetFolderByDriverAndPathAsync(Ulid driverId, string requestPath);

    Task<List<Folder>> GetFoldersByLibraryIdAsync(FolderLibraryDto[] folderLibraries);

    Task<List<Folder>> GetFoldersByLibraryIdAsync(Ulid libraryId);

    Task<Folder?> GetFolderById(Ulid folderId);

    Task<Folder?> GetFolderByPath(string path);

    Task<int> AddFolderAsync(Folder folder);

    Task<int> AddFolderLibraryAsync(FolderLibrary folderLibrary);

    Task<int> AddFolderLibraryAsync(FolderLibrary[] folderLibraries);

    Task<int> UpdateFolderAsync(Folder folder);

    Task<int> DeleteFolderAsync(Folder folder);

    Task<int> SyncFolderLibraryAsync(FolderLibrary[] folderLibraries, List<Folder> folders);

    Task<List<Folder>> GetAllFoldersAsync(CancellationToken ct = default);

    Task<List<Folder>> GetStorageFoldersAsync(CancellationToken ct = default);
}
