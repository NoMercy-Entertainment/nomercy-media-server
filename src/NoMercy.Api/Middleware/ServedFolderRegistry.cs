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
using System.Collections.Concurrent;

namespace NoMercy.Api.Middleware;

public sealed class ServedFolderRegistry : IServedFolderRegistry
{
    private readonly ConcurrentDictionary<Ulid, FolderRef> _folders = new();

    public void Add(Ulid folderId, Ulid driverId, string subPath)
    {
        _folders[folderId] = new(driverId, subPath ?? string.Empty);
    }

    public void Remove(Ulid folderId)
    {
        _folders.TryRemove(folderId, out _);
    }

    public bool TryGet(Ulid folderId, out FolderRef folderRef) =>
        _folders.TryGetValue(folderId, out folderRef);
}
