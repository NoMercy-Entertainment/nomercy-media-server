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
/// The fields of a Windows CD-ROM <see cref="DriveInfo"/> the scanner needs,
/// captured once so they can be faked in tests. <paramref name="VolumeLabel"/>
/// is only read when <paramref name="IsReady"/> is true; it may be empty.
/// </summary>
public sealed record CdRomDriveSnapshot(string Name, bool IsReady, string VolumeLabel);
