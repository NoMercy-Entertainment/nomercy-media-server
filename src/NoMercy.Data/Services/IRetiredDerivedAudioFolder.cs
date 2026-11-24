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

public interface IRetiredDerivedAudioFolder
{
    /// <summary>
    /// Removes the folder the server once kept derived audio in, with all it
    /// holds. Returns the bytes that were given back, or <c>null</c> when
    /// there was no such folder.
    /// </summary>
    Task<long?> RemoveAsync(CancellationToken cancellationToken);
}
