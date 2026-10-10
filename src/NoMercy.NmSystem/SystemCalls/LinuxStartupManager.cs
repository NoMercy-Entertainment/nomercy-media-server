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

using System.Runtime.Versioning;
using NoMercy.NmSystem.Information;

namespace NoMercy.NmSystem.SystemCalls;

internal static class LinuxStartupManager
{
    [SupportedOSPlatform("linux")]
    public static bool IsLinuxStartupEnabled()
    {
        try
        {
            bool desktopAutostartAvailable =
                Screen.IsDesktopEnvironment()
                && StartupManagerShared.ResolveLauncherPath() is not null
                && File.Exists(GetXdgAutostartPath());
            return IsLinuxStartupEnabled(LinuxSystemdService.Current, desktopAutostartAvailable);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsLinuxStartupEnabled(
        LinuxSystemdService service,
        bool desktopAutostartAvailable
    ) => desktopAutostartAvailable || service.IsEnabled(Environment.UserName);

    /// <summary>
    /// Generates a systemd user service unit file for headless Linux.
    /// Starts the server directly with --service.
    /// </summary>
    [SupportedOSPlatform("linux")]
    public static (string Content, string Path) GenerateSystemdUnit()
    {
        string appPath = StartupManagerShared.GetExecutablePath();
        string unitPath = GetSystemdUnitPath();

        string unitContent = $"""
            [Unit]
            Description=NoMercy MediaServer
            After=network-online.target
            Wants=network-online.target

            [Service]
            Type=notify
            ExecStart={appPath} --service
            WorkingDirectory={Path.GetDirectoryName(appPath)}
            Restart=on-failure
            RestartSec=10
            StandardOutput=journal
            StandardError=journal
            SyslogIdentifier=nomercy-mediaserver
            Environment=DOTNET_ROOT=/usr/share/dotnet

            [Install]
            WantedBy=default.target
            """;

        return (unitContent, unitPath);
    }

    /// <summary>
    /// Generates an XDG autostart .desktop file for the Launcher on desktop Linux.
    /// </summary>
    [SupportedOSPlatform("linux")]
    public static (string Content, string Path) GenerateXdgAutostart(string launcherPath)
    {
        string desktopPath = GetXdgAutostartPath();

        string desktopContent = $"""
            [Desktop Entry]
            Type=Application
            Name=NoMercy Launcher
            Comment=Launcher for NoMercy MediaServer
            Exec={launcherPath}
            Icon=NoMercy-MediaServer
            Terminal=false
            StartupNotify=true
            X-GNOME-Autostart-enabled=true
            Categories=AudioVideo;Video;Player;Network;
            """;

        return (desktopContent, desktopPath);
    }

    [SupportedOSPlatform("linux")]
    public static void RegisterLinuxStartup()
    {
        try
        {
            if (Screen.IsDesktopEnvironment())
            {
                // Desktop: use XDG autostart for the Launcher
                string? launcherPath = StartupManagerShared.ResolveLauncherPath();
                if (launcherPath is not null)
                {
                    (string desktopContent, string desktopPath) = GenerateXdgAutostart(
                        launcherPath
                    );

                    string? directory = Path.GetDirectoryName(desktopPath);
                    if (!string.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);

                    File.WriteAllText(desktopPath, desktopContent);
                    Logger.App($"XDG autostart entry written to {desktopPath}");

                    // Clean up headless systemd unit if it exists
                    string unitPath = GetSystemdUnitPath();
                    if (File.Exists(unitPath))
                    {
                        LinuxSystemdService.Current.Disable();
                        File.Delete(unitPath);
                        Logger.App("Removed stale systemd unit (switched to desktop mode).");
                    }

                    return;
                }

                Logger.App(
                    "Launcher binary not found; falling back to systemd service for server."
                );
            }

            // Headless (or Launcher not found): systemd user service for the server
            (string unitContent, string unitPath2) = GenerateSystemdUnit();

            string? unitDir = Path.GetDirectoryName(unitPath2);
            if (!string.IsNullOrEmpty(unitDir))
                Directory.CreateDirectory(unitDir);

            File.WriteAllText(unitPath2, unitContent);
            Logger.App($"systemd user service unit written to {unitPath2}");
            LinuxSystemdService.Current.Enable(Environment.UserName);
            Logger.App("systemd user service enabled with lingering.");

            // Clean up desktop autostart entry if it exists
            string xdgPath = GetXdgAutostartPath();
            if (File.Exists(xdgPath))
            {
                File.Delete(xdgPath);
                Logger.App("Removed stale XDG autostart entry (switched to headless mode).");
            }
        }
        catch (Exception ex)
        {
            Logger.App($"Failed to register Linux startup: {ex.Message}");
        }
    }

    [SupportedOSPlatform("linux")]
    public static void UnregisterLinuxStartup()
    {
        try
        {
            // Remove systemd unit
            string unitPath = GetSystemdUnitPath();
            if (File.Exists(unitPath))
            {
                LinuxSystemdService.Current.Disable();
                File.Delete(unitPath);
                Logger.App("Linux systemd service unregistration successful.");
            }

            // Remove XDG autostart entry
            string xdgPath = GetXdgAutostartPath();
            if (File.Exists(xdgPath))
            {
                File.Delete(xdgPath);
                Logger.App("Linux XDG autostart unregistration successful.");
            }

            // Also remove legacy desktop entry if it exists
            string legacyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config/autostart/nomercymediaserver.desktop"
            );

            if (File.Exists(legacyPath))
            {
                File.Delete(legacyPath);
                Logger.App("Legacy Linux desktop autostart entry removed.");
            }
        }
        catch (Exception ex)
        {
            Logger.App($"Failed to unregister Linux startup: {ex.Message}");
        }
    }

    [SupportedOSPlatform("linux")]
    public static string GetSystemdUnitPath()
    {
        string configHome =
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config"
            );
        return Path.Combine(configHome, "systemd/user/nomercy-mediaserver.service");
    }

    [SupportedOSPlatform("linux")]
    public static string GetXdgAutostartPath()
    {
        string configHome =
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config"
            );
        return Path.Combine(configHome, "autostart/nomercy-launcher.desktop");
    }
}
