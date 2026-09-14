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
namespace NoMercy.Api.Middleware;

/// <summary>The folders DynamicStaticFilesMiddleware serves, keyed by the folder ULID in the URL.</summary>
public interface IServedFolderRegistry
{
    void Add(Ulid folderId, Ulid driverId, string subPath);
    void Remove(Ulid folderId);
    bool TryGet(Ulid folderId, out FolderRef folderRef);
}
