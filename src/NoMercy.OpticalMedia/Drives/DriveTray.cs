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

using NoMercy.NmSystem.SystemCalls.OpticalDrives;

namespace NoMercy.OpticalMedia.Drives;

/// <summary>
/// Resolves the route's drive path against the drives the monitor reports and
/// hands the tool the monitor's own path, so a request can never pass an
/// arbitrary device or file path to eject/drutil.
/// </summary>
public sealed class DriveTray(IDriveMonitor driveMonitor, ITrayCommands trayCommands) : IDriveTray
{
    private static readonly char[] _separators = ['\\', '/'];

    public bool Open(string drivePath)
    {
        DiscDrive? drive = FindKnownDrive(drivePath);
        return drive is not null && trayCommands.Open(drive.Path);
    }

    public bool Close(string drivePath)
    {
        DiscDrive? drive = FindKnownDrive(drivePath);
        return drive is not null && trayCommands.Close(drive.Path);
    }

    private DiscDrive? FindKnownDrive(string drivePath)
    {
        if (string.IsNullOrWhiteSpace(drivePath))
            return null;

        string requested = drivePath.TrimEnd(_separators);
        return driveMonitor
            .GetDrives()
            .FirstOrDefault(d =>
                d.Path.TrimEnd(_separators).Equals(requested, StringComparison.OrdinalIgnoreCase)
            );
    }
}
