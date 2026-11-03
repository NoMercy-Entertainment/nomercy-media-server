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

namespace NoMercy.OpticalMedia.Drives;

/// <summary>
/// Opens and closes the tray of a drive the <see cref="IDriveMonitor"/> knows.
/// A path that is not a known optical drive is refused before any OS tool runs.
/// </summary>
public interface IDriveTray
{
    bool Open(string drivePath);
    bool Close(string drivePath);
}
