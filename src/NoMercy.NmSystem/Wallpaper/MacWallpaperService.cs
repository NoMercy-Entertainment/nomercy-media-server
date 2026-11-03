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

[SupportedOSPlatform("macos")]
public class MacWallpaperService(IWallpaperCommandRunner commandRunner) : IWallpaperService
{
    public bool IsSupported => true;

    public bool Set(string imagePath, WallpaperStyle style, string hexColor)
    {
        return ApplyWallpaper(imagePath);
    }

    public bool SetSilent(string imagePath, WallpaperStyle style, string hexColor)
    {
        return Set(imagePath, style, hexColor);
    }

    public void Restore()
    {
        // macOS doesn't expose a simple API to restore the previous wallpaper.
        // The OS manages wallpaper history internally.
    }

    private bool ApplyWallpaper(string imagePath)
    {
        string quotedPath = QuoteForAppleScript(imagePath);

        // Try the System Events approach first (works on macOS 14+ Sonoma)
        string script =
            $"tell application \"System Events\" to tell every desktop to set picture to {quotedPath}";

        if (RunOsascript(script))
            return true;

        // Fallback for older macOS versions
        string fallbackScript =
            $"tell application \"Finder\" to set desktop picture to POSIX file {quotedPath}";
        return RunOsascript(fallbackScript);
    }

    private bool RunOsascript(string script)
    {
        return commandRunner.Run(BuildOsascriptStartInfo(script)) == 0;
    }

    // The script is one argv token. On Unix, .NET splits the Arguments string
    // with rules where only double quotes group, so a single-quoted script
    // reached osascript in pieces and every call failed.
    private static ProcessStartInfo BuildOsascriptStartInfo(string script)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "osascript",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add(script);
        return startInfo;
    }

    // AppleScript string literals escape backslash and double quote with a backslash.
    private static string QuoteForAppleScript(string value)
    {
        string escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }
}
