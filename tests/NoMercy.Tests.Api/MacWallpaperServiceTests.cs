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
using NoMercy.NmSystem.Wallpaper;
using Xunit;

namespace NoMercy.Tests.Api;

/// <summary>
/// On Unix .NET splits <see cref="ProcessStartInfo.Arguments"/> with rules where only
/// double quotes group, so an AppleScript wrapped in single quotes reached osascript
/// as many broken tokens and every set-wallpaper call failed silently (#429). The
/// script must travel as one argv token, and a failing osascript must be reported.
/// </summary>
[SupportedOSPlatform("macos")]
public class MacWallpaperServiceTests
{
    private const string ImagePath = "/Users/me/Pictures/my poster.jpg";
    private const string SystemEventsScript =
        "tell application \"System Events\" to tell every desktop to set picture to \"/Users/me/Pictures/my poster.jpg\"";
    private const string FinderScript =
        "tell application \"Finder\" to set desktop picture to POSIX file \"/Users/me/Pictures/my poster.jpg\"";

    [Fact]
    public void Set_PassesTheAppleScriptToOsascriptAsASingleArgument()
    {
        RecordingCommandRunner runner = new(exitCode: 0);
        MacWallpaperService service = new(runner);

        service.Set(ImagePath, WallpaperStyle.Fill, "#000000");

        ProcessStartInfo startInfo = Assert.Single(runner.Started);
        string[] expectedArguments = ["-e", SystemEventsScript];
        Assert.Equal("osascript", startInfo.FileName);
        Assert.Equal(expectedArguments, startInfo.ArgumentList);
        Assert.Equal(string.Empty, startInfo.Arguments);
    }

    [Fact]
    public void Set_WhenSystemEventsFails_FallsBackToFinderAsASingleArgument()
    {
        RecordingCommandRunner runner = new(exitCode: 1);
        MacWallpaperService service = new(runner);

        service.Set(ImagePath, WallpaperStyle.Fill, "#000000");

        string[] expectedArguments = ["-e", FinderScript];
        Assert.Equal(2, runner.Started.Count);
        Assert.Equal(expectedArguments, runner.Started[1].ArgumentList);
        Assert.Equal(string.Empty, runner.Started[1].Arguments);
    }

    [Fact]
    public void Set_WhenOsascriptExitsNonZero_ReportsFailure()
    {
        RecordingCommandRunner runner = new(exitCode: 1);
        MacWallpaperService service = new(runner);

        bool applied = service.Set(ImagePath, WallpaperStyle.Fill, "#000000");

        Assert.False(applied);
    }

    [Fact]
    public void Set_WhenOsascriptSucceeds_ReportsSuccessWithoutTheFallback()
    {
        RecordingCommandRunner runner = new(exitCode: 0);
        MacWallpaperService service = new(runner);

        bool applied = service.SetSilent(ImagePath, WallpaperStyle.Fill, "#000000");

        Assert.True(applied);
        Assert.Single(runner.Started);
    }

    [Fact]
    public void Set_EscapesQuotesAndBackslashesInTheImagePath()
    {
        RecordingCommandRunner runner = new(exitCode: 0);
        MacWallpaperService service = new(runner);

        service.Set("/Users/me/say \"hi\"\\now.jpg", WallpaperStyle.Fill, "#000000");

        ProcessStartInfo startInfo = Assert.Single(runner.Started);
        Assert.Equal(
            "tell application \"System Events\" to tell every desktop to set picture to \"/Users/me/say \\\"hi\\\"\\\\now.jpg\"",
            startInfo.ArgumentList[1]
        );
    }

    private sealed class RecordingCommandRunner(int exitCode) : IWallpaperCommandRunner
    {
        public List<ProcessStartInfo> Started { get; } = [];

        public int Run(ProcessStartInfo startInfo)
        {
            Started.Add(startInfo);
            return exitCode;
        }
    }
}
