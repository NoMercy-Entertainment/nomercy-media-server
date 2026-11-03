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

namespace NoMercy.NmSystem.SystemCalls.OpticalDrives;

/// <summary>
/// Opens and closes an optical drive tray. Both return true only when the OS
/// tool reported success; a failed or missing tool is false, never an exception.
/// </summary>
public interface ITrayCommands
{
    bool Open(string drivePath);
    bool Close(string drivePath);
}
