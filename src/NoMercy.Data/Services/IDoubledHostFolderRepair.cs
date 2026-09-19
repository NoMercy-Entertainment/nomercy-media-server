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

public interface IDoubledHostFolderRepair
{
    /// <summary>
    /// Rewrites every track whose <c>HostFolder</c> holds the same folder twice
    /// back to the single folder, and returns how many rows were rewritten.
    /// </summary>
    Task<int> RunAsync(CancellationToken cancellationToken);
}
