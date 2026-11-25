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

namespace NoMercy.Queue.MediaServer.Jobs;

/// <summary>
/// The work of a nightly maintenance job, run now and reporting what it freed.
/// The caller holds the <see cref="MaintenanceGate"/>.
/// </summary>
public interface IMaintenancePass<TResult>
{
    Task<TResult> RunAsync(CancellationToken cancellationToken);
}
