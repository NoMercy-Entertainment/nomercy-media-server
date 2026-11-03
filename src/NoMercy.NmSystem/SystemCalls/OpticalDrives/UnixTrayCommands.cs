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
/// Tray control on Linux (<c>eject</c>) and macOS (<c>drutil</c>). The device
/// path is passed as a single argv token, and success is the tool's exit code.
/// </summary>
public sealed class UnixTrayCommands(ICommandRunner commandRunner, bool isMac) : ITrayCommands
{
    public bool Open(string drivePath) =>
        isMac ? Run("drutil", ["eject", drivePath]) : Run("eject", [drivePath]);

    public bool Close(string drivePath) =>
        isMac ? Run("drutil", ["tray", "close", drivePath]) : Run("eject", ["-t", drivePath]);

    private bool Run(string executable, string[] arguments)
    {
        string commandLine = $"{executable} {string.Join(' ', arguments)}";
        try
        {
            Shell.ExecResult result = commandRunner.Run(executable, arguments);
            if (result.Success)
                return true;

            Logger.Error(
                $"[Optical] '{commandLine}' exited with {result.ExitCode}: {result.StandardError.Trim()}"
            );
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error($"[Optical] '{commandLine}' could not run: {ex.Message}");
            return false;
        }
    }
}
