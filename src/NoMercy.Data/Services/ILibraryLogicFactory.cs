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

namespace NoMercy.Data.Services;

/// <summary>
/// Creates a <see cref="LibraryLogic"/> for one library rescan. A factory rather than a
/// direct DI registration because <see cref="LibraryLogic"/> takes the library's
/// <see cref="Ulid"/> at construction time, which is only known per-request.
/// </summary>
public interface ILibraryLogicFactory
{
    LibraryLogic Create(Ulid libraryId);
}
