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
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.SystemCalls.OpticalDrives;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.NmSystem.SystemCalls;

public static class Optical
{
    private static readonly ICommandRunner _commandRunner = new ShellCommandRunner();

    // LOCAL-ONLY: discs are always mounted on this machine, never on a remote driver.
    private static readonly IStorageDriver _localStorage = new LocalStorageDriver();

    public static IReadOnlyList<OpticalDriveState> GetOpticalDrives()
    {
        if (Software.IsWindows)
            return new WindowsOpticalDriveScanner(new DriveInfoCdRomDriveSource()).GetDrives();
        if (Software.IsLinux)
            return new LinuxOpticalDriveScanner(_commandRunner).GetDrives();
        if (Software.IsMac)
            return new MacOpticalDriveScanner(_commandRunner).GetDrives();

        throw new PlatformNotSupportedException("Unsupported OS.");
    }

    public static bool OpenDrive(string drivePath)
    {
        if (Software.IsWindows)
            return OpenWindowsOpticalDrives(drivePath);
        if (Software.IsLinux)
            return OpenLinuxOpticalDrives(drivePath);
        if (Software.IsMac)
            return OpenMacOpticalDrives(drivePath);

        throw new PlatformNotSupportedException("Unsupported OS.");
    }

    public static bool CloseDrive(string drivePath)
    {
        if (Software.IsWindows)
            return CloseWindowsOpticalDrives(drivePath);
        if (Software.IsLinux)
            return CloseLinuxOpticalDrives(drivePath);
        if (Software.IsMac)
            return CloseMacOpticalDrives(drivePath);

        throw new PlatformNotSupportedException("Unsupported OS.");
    }

    #region Windows Optical Drive Control

    [DllImport("winmm.dll", EntryPoint = "mciSendString")]
    public static extern int mciSendString(
        string lpstrCommand,
        string lpstrReturnString,
        int uReturnLength,
        int hwndCallback
    );

    private static bool OpenWindowsOpticalDrives(string drivePath)
    {
        if (!IsOpticalDrive(drivePath))
            return false; // Early check

        try
        {
            int locked = mciSendString(
                $"open {drivePath[0]}: type CDAudio alias drive{drivePath[0]}",
                string.Empty,
                0,
                0
            );
            if (locked != 0)
                return false; // Check if open was successful

            int result = mciSendString($"set drive{drivePath[0]} door open", string.Empty, 0, 0);
            int released = mciSendString($"close drive{drivePath[0]}", string.Empty, 0, 0);

            return result == 0;
        }
        catch (Exception ex)
        {
            Logger.Error(
                $"[ERROR] Failed to open Windows optical drive '{drivePath}': {ex.Message}"
            );
            return false;
        }
    }

    private static bool CloseWindowsOpticalDrives(string drivePath)
    {
        if (!IsOpticalDrive(drivePath))
            return false; //Early check

        try
        {
            int locked = mciSendString(
                $"open {drivePath[0]}: type CDAudio alias drive{drivePath[0]}",
                string.Empty,
                0,
                0
            );
            if (locked != 0)
                return false; // check if open was successful

            int result = mciSendString($"set drive{drivePath[0]} door closed", string.Empty, 0, 0);
            int released = mciSendString($"close drive{drivePath[0]}", string.Empty, 0, 0);

            return result == 0;
        }
        catch (Exception ex)
        {
            Logger.Error(
                $"[ERROR] Failed to close Windows optical drive '{drivePath}': {ex.Message}"
            );
            return false;
        }
    }

    private static bool IsOpticalDrive(string drivePath)
    {
        DriveInfo driveInfo = new(drivePath);
        return driveInfo.DriveType == DriveType.CDRom;
    }

    #endregion

    #region Linux Optical Drive Control

    private static bool OpenLinuxOpticalDrives(string drivePath)
    {
        try
        {
            Shell.ExecSync("eject", [drivePath]);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[ERROR] Failed to open Linux optical drive '{drivePath}': {ex.Message}");
            return false;
        }
    }

    private static bool CloseLinuxOpticalDrives(string drivePath)
    {
        try
        {
            Shell.ExecSync("eject", ["-t", drivePath]);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(
                $"[ERROR] Failed to close Linux optical drive '{drivePath}': {ex.Message}"
            );
            return false;
        }
    }

    #endregion

    #region macOS Optical Drive Control

    private static bool OpenMacOpticalDrives(string drivePath)
    {
        try
        {
            Shell.ExecSync("drutil", ["eject", drivePath]);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[ERROR] Failed to open macOS optical drive '{drivePath}': {ex.Message}");
            return false;
        }
    }

    private static bool CloseMacOpticalDrives(string drivePath)
    {
        try
        {
            Shell.ExecSync("drutil", ["tray", "close", drivePath]);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(
                $"[ERROR] Failed to close macOS optical drive '{drivePath}': {ex.Message}"
            );
            return false;
        }
    }

    #endregion

    public static OpticalDiscType GetDiscType(OpticalDriveState drive) =>
        new OpticalDiscTypeResolver(_localStorage).Resolve(drive);
}
