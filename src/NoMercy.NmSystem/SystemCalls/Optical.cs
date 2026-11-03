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

    public static Dictionary<string, string?> GetOpticalDrives()
    {
        if (Software.IsWindows)
            return GetWindowsOpticalDrives();
        if (Software.IsLinux)
            return GetLinuxOpticalDrives();
        if (Software.IsMac)
            return GetMacOpticalDrives();

        throw new PlatformNotSupportedException("Unsupported OS.");
    }

    private static Dictionary<string, string?> GetWindowsOpticalDrives()
    {
        Dictionary<string, string?> drives = new();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
            if (drive is { DriveType: DriveType.CDRom, IsReady: true })
                drives[drive.Name] = drive.VolumeLabel.Length > 0 ? drive.VolumeLabel : null;
            else if (drive.DriveType == DriveType.CDRom)
                drives[drive.Name] = null;

        return drives;
    }

    private static Dictionary<string, string?> GetLinuxOpticalDrives() =>
        new LinuxOpticalDriveScanner(_commandRunner)
            .GetDrives()
            .ToDictionary(drive => drive.Path, drive => drive.HasDisc ? drive.Label : null);

    private static Dictionary<string, string?> GetMacOpticalDrives() =>
        new MacOpticalDriveScanner(_commandRunner)
            .GetDrives()
            .ToDictionary(drive => drive.Path, drive => drive.HasDisc ? drive.Label : null);

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

    public static OpticalDiscType GetDiscType(string drivePath)
    {
        // LOCAL-ONLY: Optical is a static class in NmSystem; no reference to NoMercy.Providers.
        IStorageDriver driver = new LocalStorageDriver();

        if (!driver.DirectoryExists(drivePath))
            return OpticalDiscType.None;

        // Check for Blu-ray
        if (driver.DirectoryExists(Path.Combine(drivePath, "BDMV")))
            return OpticalDiscType.BluRay;

        // Check for DVD
        if (driver.DirectoryExists(Path.Combine(drivePath, "VIDEO_TS")))
            return OpticalDiscType.Dvd;

        // Check for CD (Audio CD or Data CD)
        try
        {
            DriveInfo drive = new(drivePath);
            if (drive is { DriveType: DriveType.CDRom, IsReady: true })
                // If we get here and it's not BD or DVD, it's some form of CD
                return OpticalDiscType.Cd;
        }
        catch
        {
            return OpticalDiscType.None;
        }

        return OpticalDiscType.None;
    }
}
