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

namespace NoMercy.Encoder.Orchestration;

/// <summary>
/// Answers which working folders under the transcode root belong to a queued
/// or running encoder job, so the boot sweep keeps them.
/// </summary>
public interface IEncoderTempDirectoryOwners
{
    Task<IReadOnlyCollection<string>> GetOwnedTempDirectoriesAsync(CancellationToken ct);
}
