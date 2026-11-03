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

using NoMercy.NmSystem.Dto;

namespace NoMercy.NmSystem.SystemCalls.OpticalDrives;

/// <summary>
/// Lists the optical drives of one platform. Implementations never throw for
/// "no drives" or "tool missing": both are an empty list.
/// </summary>
public interface IOpticalDriveScanner
{
    IReadOnlyList<OpticalDriveState> GetDrives();
}
