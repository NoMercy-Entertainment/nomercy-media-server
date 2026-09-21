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
using System.Runtime.Versioning;

namespace NoMercy.NmSystem.Wallpaper;

[SupportedOSPlatform("linux")]
public class LinuxWallpaperService : IWallpaperService
{
    private string? _previousWallpaper;
    private string? _previousColor;

    public bool IsSupported
    {
        get
        {
            string? display = Environment.GetEnvironmentVariable("DISPLAY");
            string? wayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
            return display is not null || wayland is not null;
        }
    }

    public void Set(string imagePath, WallpaperStyle style, string hexColor)
    {
        if (!IsSupported)
            return;

        SaveCurrentWallpaper();
        ApplyColor(hexColor);
        ApplyWallpaper(imagePath, style);
    }

    public void SetSilent(string imagePath, WallpaperStyle style, string hexColor)
    {
        Set(imagePath, style, hexColor);
    }

    public void Restore()
    {
        if (!IsSupported)
            return;

        if (_previousWallpaper is not null)
            ApplyWallpaper(_previousWallpaper, WallpaperStyle.Fill);

        if (_previousColor is not null)
            ApplyColor(_previousColor);

        _previousWallpaper = null;
        _previousColor = null;
    }

    private void SaveCurrentWallpaper()
    {
        DesktopEnvironment de = DetectDesktopEnvironment();
        if (de == DesktopEnvironment.Gnome)
        {
            _previousWallpaper = RunCommand(
                "gsettings",
                ["get", "org.gnome.desktop.background", "picture-uri"]
            )
                ?.Trim()
                .Trim('\'');
            _previousColor = RunCommand(
                "gsettings",
                ["get", "org.gnome.desktop.background", "primary-color"]
            )
                ?.Trim()
                .Trim('\'');
        }
    }

    private static void ApplyWallpaper(string imagePath, WallpaperStyle style)
    {
        DesktopEnvironment de = DetectDesktopEnvironment();

        switch (de)
        {
            case DesktopEnvironment.Gnome:
                string gnomeStyle = MapStyleToGnome(style);
                RunCommand(
                    "gsettings",
                    ["set", "org.gnome.desktop.background", "picture-options", gnomeStyle]
                );
                RunCommand(
                    "gsettings",
                    ["set", "org.gnome.desktop.background", "picture-uri", $"file://{imagePath}"]
                );
                RunCommand(
                    "gsettings",
                    [
                        "set",
                        "org.gnome.desktop.background",
                        "picture-uri-dark",
                        $"file://{imagePath}",
                    ]
                );
                break;

            case DesktopEnvironment.Kde:
                string kdeScript =
                    "var allDesktops = desktops();"
                    + "for (var i = 0; i < allDesktops.length; i++) {"
                    + "  var d = allDesktops[i];"
                    + "  d.wallpaperPlugin = 'org.kde.image';"
                    + "  d.currentConfigGroup = Array('Wallpaper','org.kde.image','General');"
                    + $"  d.writeConfig('Image', 'file://{imagePath}');"
                    + "}";
                RunCommand(
                    "qdbus",
                    [
                        "org.kde.plasmashell",
                        "/PlasmaShell",
                        "org.kde.PlasmaShell.evaluateScript",
                        kdeScript,
                    ]
                );
                break;

            case DesktopEnvironment.Xfce:
                RunCommand(
                    "xfconf-query",
                    [
                        "-c",
                        "xfce4-desktop",
                        "-p",
                        "/backdrop/screen0/monitor0/workspace0/last-image",
                        "-s",
                        imagePath,
                    ]
                );
                break;

            case DesktopEnvironment.Fallback:
                RunCommand("feh", ["--bg-fill", imagePath]);
                break;
        }
    }

    private static void ApplyColor(string hexColor)
    {
        DesktopEnvironment de = DetectDesktopEnvironment();

        if (de == DesktopEnvironment.Gnome)
        {
            RunCommand(
                "gsettings",
                ["set", "org.gnome.desktop.background", "primary-color", hexColor]
            );
        }
    }

    public static DesktopEnvironment DetectDesktopEnvironment()
    {
        string? xdgDesktop = Environment
            .GetEnvironmentVariable("XDG_CURRENT_DESKTOP")
            ?.ToUpperInvariant();

        if (xdgDesktop is not null)
        {
            if (xdgDesktop.Contains("GNOME") || xdgDesktop.Contains("UNITY"))
                return DesktopEnvironment.Gnome;
            if (xdgDesktop.Contains("KDE"))
                return DesktopEnvironment.Kde;
            if (xdgDesktop.Contains("XFCE"))
                return DesktopEnvironment.Xfce;
        }

        return DesktopEnvironment.Fallback;
    }

    public static string MapStyleToGnome(WallpaperStyle style)
    {
        return style switch
        {
            WallpaperStyle.Fill => "zoom",
            WallpaperStyle.Fit => "scaled",
            WallpaperStyle.Stretch => "stretched",
            WallpaperStyle.Tile => "wallpaper",
            WallpaperStyle.Center => "centered",
            WallpaperStyle.Span => "spanned",
            _ => "zoom",
        };
    }

    // One argv token per entry: the image path and colour come from the user's
    // own choice, and a token is never re-parsed, so nothing in it can add a flag.
    private static string? RunCommand(string command, IReadOnlyList<string> arguments)
    {
        try
        {
            using Process process = new();
            process.StartInfo = new()
            {
                FileName = command,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in arguments)
                process.StartInfo.ArgumentList.Add(argument);

            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            return output;
        }
        catch
        {
            return null;
        }
    }

    public enum DesktopEnvironment
    {
        Gnome,
        Kde,
        Xfce,
        Fallback,
    }
}
