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
/// Lists Windows CD-ROM drives. A drive holds a disc exactly when it is ready;
/// the volume label is optional metadata (burned discs often have none), and
/// the drive root doubles as the mount point.
/// </summary>
public sealed class WindowsOpticalDriveScanner(ICdRomDriveSource driveSource) : IOpticalDriveScanner
{
    public IReadOnlyList<OpticalDriveState> GetDrives() =>
        [
            .. driveSource
                .GetCdRomDrives()
                .Select(drive => new OpticalDriveState(
                    Path: drive.Name,
                    Label: drive.IsReady && drive.VolumeLabel.Length > 0 ? drive.VolumeLabel : null,
                    HasDisc: drive.IsReady,
                    MountPoint: drive.IsReady ? drive.Name : null
                )),
        ];
}
