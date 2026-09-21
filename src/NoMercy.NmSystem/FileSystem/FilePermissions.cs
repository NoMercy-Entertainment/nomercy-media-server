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

using System.Runtime.InteropServices;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using Serilog.Events;

namespace NoMercy.NmSystem.FileSystem;

public class FilePermissions
{
    public static async Task SetExecutionPermissions(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // LOCAL-ONLY: FilePermissions is a static class in NmSystem; no reference to NoMercy.Providers.
            IStorageDriver driver = new LocalStorageDriver();

            if (driver.FileExists(path))
                await Shell.ExecAsync("chmod", ["+x", path]);
            else if (driver.DirectoryExists(path))
                await Shell.ExecAsync("chmod", ["-R", "+x", path]);

            Logger.System($"Set execution permissions for {path}", LogEventLevel.Verbose);
        }
    }
}
