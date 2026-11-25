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

namespace NoMercy.Api.Services;

public interface IStorageCleanupService
{
    /// <summary>
    /// Runs the nightly maintenance passes now. Null, with nothing run, when a
    /// clean-up or a nightly pass is already running.
    /// </summary>
    Task<StorageCleanupResult?> RunAsync(
        bool includeHandMadeCopies,
        CancellationToken cancellationToken
    );
}
