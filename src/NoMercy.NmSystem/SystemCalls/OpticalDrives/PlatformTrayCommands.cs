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
/// Production <see cref="ITrayCommands"/>: the host platform's tray control
/// (MCI on Windows, eject on Linux, drutil on macOS) via <see cref="Optical"/>.
/// </summary>
public sealed class PlatformTrayCommands : ITrayCommands
{
    public bool Open(string drivePath) => Optical.OpenDrive(drivePath);

    public bool Close(string drivePath) => Optical.CloseDrive(drivePath);
}
