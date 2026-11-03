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

/// <summary>Production <see cref="ICdRomDriveSource"/> backed by <see cref="DriveInfo"/>.</summary>
public sealed class DriveInfoCdRomDriveSource : ICdRomDriveSource
{
    public IReadOnlyList<CdRomDriveSnapshot> GetCdRomDrives()
    {
        List<CdRomDriveSnapshot> drives = [];

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.CDRom)
                continue;

            bool isReady = drive.IsReady;
            drives.Add(new(drive.Name, isReady, isReady ? drive.VolumeLabel : string.Empty));
        }

        return drives;
    }
}
