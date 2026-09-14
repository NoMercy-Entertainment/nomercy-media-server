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

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NoMercy.Database;
using NoMercy.Storage;

namespace NoMercy.Data.Services;

public class LibraryLogicFactory(
    IDbContextFactory<MediaContext> mediaContextFactory,
    IStorageDriver storageDriver,
    IStorageFactory storageFactory,
    ILogger<LibraryLogic> logger
) : ILibraryLogicFactory
{
    public LibraryLogic Create(Ulid libraryId) =>
        new(libraryId, mediaContextFactory, storageDriver, storageFactory, logger);
}
