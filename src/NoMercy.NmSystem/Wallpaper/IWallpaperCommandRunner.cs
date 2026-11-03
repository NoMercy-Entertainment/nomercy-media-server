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

using System.Diagnostics;

namespace NoMercy.NmSystem.Wallpaper;

/// <summary>
/// Starts a desktop command on behalf of a wallpaper service, so a test can
/// inspect the <see cref="ProcessStartInfo"/> a service builds without a desktop.
/// </summary>
public interface IWallpaperCommandRunner
{
    /// <summary>
    /// Runs the command to completion and returns its exit code. A command that
    /// cannot start or does not exit in time reports a non-zero code.
    /// </summary>
    int Run(ProcessStartInfo startInfo);
}
