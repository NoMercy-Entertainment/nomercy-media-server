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
using System.Runtime.InteropServices;
using System.Text;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Setup.Ui;

public static class DesktopIconCreator
{
    private const int MacIconTimeoutMs = 10_000;

    // LOCAL-ONLY: DesktopIconCreator is in NoMercy.Setup which cannot reference NoMercy.Providers (circular).
    private static IStorageDriver _driver => new LocalStorageDriver();

    public static void CreateDesktopIcon(string appName, string appPath, string iconPath) =>
        CreateDesktopIcon(
            appName,
            appPath,
            iconPath,
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        );

    /// <summary>
    /// Internal (not private): threading the desktop directory through as a parameter
    /// lets NoMercy.Tests.Setup exercise the real per-platform shortcut-writing branches
    /// against an isolated temp directory instead of either skipping this method entirely
    /// or — worse — actually writing a shortcut onto the real Desktop of whatever machine
    /// runs the test suite. The public overload above preserves the exact production
    /// default, so no caller behavior changes.
    /// </summary>
    internal static void CreateDesktopIcon(
        string appName,
        string appPath,
        string iconPath,
        string desktopPath
    )
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                CreateWindowsShortcut(appName, appPath, iconPath, desktopPath);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                CreateMacShortcut(appName, appPath, iconPath, desktopPath);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                CreateLinuxShortcut(appName, appPath, iconPath, desktopPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error creating desktop icon: {ex.Message}");
        }
    }

    private static void CreateWindowsShortcut(
        string appName,
        string appPath,
        string iconPath,
        string desktopPath
    )
    {
#pragma warning disable CA1416
        try
        {
            string shortcutPath = Path.Combine(desktopPath, $"{appName}.lnk");

            Type? id = Type.GetTypeFromProgID("WScript.Shell");
            if (id == null)
                return;

            dynamic shell = Activator.CreateInstance(id) ?? throw new InvalidOperationException();
            if (shell == null)
                return;

            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = appPath;
            shortcut.IconLocation = iconPath;
            shortcut.Save();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error creating Windows shortcut: {ex.Message}");
        }
#pragma warning restore CA1416
    }

    private static void CreateMacShortcut(
        string appName,
        string appPath,
        string iconPath,
        string desktopPath
    )
    {
        try
        {
            string aliasPath = Path.Combine(desktopPath, appName);

            string script =
                $@"
            tell application ""Finder""
                set appAlias to make new alias file at POSIX file ""{desktopPath}"" to POSIX file ""{appPath}""
                set name of appAlias to ""{appName}""
            end tell";

            string scriptPath = "/tmp/CreateShortcut.scpt";
            using (Stream scriptStream = _driver.OpenWrite(scriptPath, overwrite: true))
            using (StreamWriter scriptWriter = new(scriptStream, Encoding.UTF8, leaveOpen: true))
                scriptWriter.Write(script);
            using (Process? osascriptProc = Process.Start("osascript", scriptPath))
                osascriptProc.WaitForExit();

            if (string.IsNullOrEmpty(iconPath) || !_driver.FileExists(iconPath))
                return;

            if (!_driver.FileExists(aliasPath))
            {
                Console.WriteLine(
                    $"Could not set the Mac shortcut icon: no alias was created at {aliasPath}"
                );
                return;
            }

            ApplyMacIcon(iconPath, aliasPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error creating Mac shortcut: {ex.Message}");
        }
    }

    private static void ApplyMacIcon(string iconPath, string aliasPath)
    {
        ProcessStartInfo startInfo = BuildMacIconCommand(iconPath, aliasPath);
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = true;

        using Process? process = Process.Start(startInfo);
        if (process is null)
        {
            Console.WriteLine("Could not set the Mac shortcut icon: osascript did not start");
            return;
        }

        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(MacIconTimeoutMs))
        {
            process.Kill();
            Console.WriteLine("Could not set the Mac shortcut icon: osascript timed out");
            return;
        }

        if (process.ExitCode != 0)
            Console.WriteLine($"Could not set the Mac shortcut icon: {stderr.Result.Trim()}");
    }

    /// <summary>
    /// Builds the <c>osascript</c> call that gives the Finder alias at
    /// <paramref name="aliasPath"/> the custom icon in <paramref name="iconPath"/>.
    /// An alias is a plain file, so the icon is set on the file itself through
    /// <c>NSWorkspace setIcon:forFile:</c>. Both paths are passed as script
    /// arguments, never spliced into the script text, so quotes in a path are harmless.
    /// </summary>
    internal static ProcessStartInfo BuildMacIconCommand(string iconPath, string aliasPath)
    {
        string[] scriptLines =
        [
            "use framework \"AppKit\"",
            "on run argv",
            "set iconImage to current application's NSImage's alloc()'s initWithContentsOfFile:(item 1 of argv)",
            "if iconImage is missing value then error \"cannot read icon \" & (item 1 of argv)",
            "set applied to current application's NSWorkspace's sharedWorkspace()'s setIcon:iconImage forFile:(item 2 of argv) options:0",
            "if applied is false then error \"Finder refused the icon for \" & (item 2 of argv)",
            "end run",
        ];

        ProcessStartInfo startInfo = new("osascript");
        foreach (string line in scriptLines)
        {
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add(line);
        }
        startInfo.ArgumentList.Add(iconPath);
        startInfo.ArgumentList.Add(aliasPath);
        return startInfo;
    }

    private static void CreateLinuxShortcut(
        string appName,
        string appPath,
        string iconPath,
        string desktopPath
    )
    {
        try
        {
            string shortcutPath = Path.Combine(desktopPath, $"{appName}.desktop");

            string content =
                $@"
                [Desktop Entry]
                Name={appName}
                Exec={appPath}
                Icon={iconPath}
                Type=Application
                Terminal=false";

            using (Stream shortcutStream = _driver.OpenWrite(shortcutPath, overwrite: true))
            using (
                StreamWriter shortcutWriter = new(shortcutStream, Encoding.UTF8, leaveOpen: true)
            )
                shortcutWriter.Write(content);
            using (Process? chmodProc = Process.Start("chmod", $"+x \"{shortcutPath}\""))
                chmodProc.WaitForExit();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error creating Linux shortcut: {ex.Message}");
        }
    }
}
