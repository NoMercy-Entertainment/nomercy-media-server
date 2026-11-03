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

using System.ComponentModel;
using System.Diagnostics;

namespace NoMercy.NmSystem.Wallpaper;

public sealed class WallpaperCommandRunner : IWallpaperCommandRunner
{
    private const int FailedExitCode = -1;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public int Run(ProcessStartInfo startInfo)
    {
        try
        {
            using Process process = new();
            process.StartInfo = startInfo;
            process.Start();

            if (process.WaitForExit(Timeout))
                return process.ExitCode;

            process.Kill();
            return FailedExitCode;
        }
        catch (Win32Exception)
        {
            return FailedExitCode;
        }
        catch (InvalidOperationException)
        {
            return FailedExitCode;
        }
    }
}
