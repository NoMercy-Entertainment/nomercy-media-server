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
using NoMercy.Setup.Ui;

namespace NoMercy.Tests.Setup.Ui;

/// <summary>
/// Requirement: creating a desktop shortcut for the app must never touch the real
/// Desktop folder of the machine running the process — every platform branch takes an
/// explicit destination directory (see the internal <c>desktopPath</c> overload added
/// specifically for this) — and any failure (missing WScript.Shell, I/O error) must be
/// swallowed so a first-boot desktop-icon failure never blocks the rest of setup.
/// </summary>
/// <remarks>
/// Runs on Windows locally — <see cref="DesktopIconCreator.CreateDesktopIcon"/>'s
/// Windows branch (real <c>.lnk</c> creation via <c>WScript.Shell</c>) is exercised
/// directly; the macOS/Linux branches are covered by the project's CI on those
/// platforms.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class DesktopIconCreatorTests : IDisposable
{
    private readonly string _tempDesktop;

    public DesktopIconCreatorTests()
    {
        _tempDesktop = Path.Combine(Path.GetTempPath(), $"nm-desktop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDesktop);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDesktop))
                Directory.Delete(_tempDesktop, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void CreateDesktopIcon_Windows_CreatesLnkFileInSpecifiedDirectory_NotRealDesktop()
    {
        if (!OperatingSystem.IsWindows())
            return; // this branch is Windows-only; CI covers the other platforms.

        string appPath = Path.Combine(_tempDesktop, "NoMercyMediaServer.exe");
        string iconPath = Path.Combine(_tempDesktop, "icon.ico");

        DesktopIconCreator.CreateDesktopIcon("NoMercy Test App", appPath, iconPath, _tempDesktop);

        string expectedShortcut = Path.Combine(_tempDesktop, "NoMercy Test App.lnk");
        Assert.True(
            File.Exists(expectedShortcut),
            $"expected shortcut at {expectedShortcut} in the isolated temp dir, never the real Desktop"
        );
    }

    // NOTE: the public (no desktopPath) overload is deliberately NOT exercised here —
    // calling it writes a REAL .lnk file onto whatever machine runs this suite's actual
    // Desktop folder (verified and cleaned up manually once during development of this
    // test; do not reintroduce). Its only logic is delegating to
    // Environment.GetFolderPath(SpecialFolder.Desktop) plus the internal overload
    // this file already covers directly — see BinariesDownloadMethodsTests-style
    // "public overload forwards to the internal one" reasoning; a one-line forwarding
    // method is not worth risking a real side effect on the developer's Desktop.

    [Fact]
    public void CreateDesktopIcon_NonexistentDesktopDirectory_DoesNotThrow()
    {
        string missingDir = Path.Combine(_tempDesktop, "does-not-exist");
        string appPath = Path.Combine(_tempDesktop, "app.exe");
        string iconPath = Path.Combine(_tempDesktop, "icon.ico");

        // CreateWindowsShortcut's own try/catch must absorb the failure (WScript.Shell
        // creating a .lnk under a non-existent directory) rather than throwing.
        DesktopIconCreator.CreateDesktopIcon("Test App", appPath, iconPath, missingDir);
    }

    [Fact]
    public void CreateDesktopIcon_UnsupportedPlatformSimulated_ViaEmptyAppName_DoesNotThrow()
    {
        // Exercises the general try/catch wrapper with unusual (but not literally
        // invalid on any single platform) inputs — an empty app name still produces a
        // shortcut filename of ".lnk", which some filesystems reject; must not throw.
        string appPath = Path.Combine(_tempDesktop, "app.exe");
        string iconPath = Path.Combine(_tempDesktop, "icon.ico");

        DesktopIconCreator.CreateDesktopIcon(string.Empty, appPath, iconPath, _tempDesktop);
    }

    [Fact]
    public void BuildMacIconCommand_TargetsTheAliasFileItself_NeverAPathInsideIt()
    {
        // A Finder alias is a plain file, so "<alias>/Icon.icns" can never exist.
        const string aliasPath = "/Users/me/Desktop/NoMercy MediaServer";
        const string iconPath = "/Applications/NoMercy.app/Contents/Resources/icon.icns";

        ProcessStartInfo command = DesktopIconCreator.BuildMacIconCommand(iconPath, aliasPath);

        command
            .ArgumentList.Should()
            .NotContain(
                argument => argument.Contains(aliasPath + "/"),
                "the icon must be applied to the alias file itself, not copied into it as if it were a folder"
            );
        command.ArgumentList.Should().Contain(aliasPath);
        command.ArgumentList.Should().Contain(iconPath);
    }

    [Fact]
    public void BuildMacIconCommand_PassesPathsAsArguments_NotInsideTheScriptText()
    {
        // A quote in a path must not be able to break out of the script.
        const string aliasPath = "/Users/o'brien/Desktop/NoMercy";
        const string iconPath = "/tmp/icon \"x\".icns";

        ProcessStartInfo command = DesktopIconCreator.BuildMacIconCommand(iconPath, aliasPath);

        command.FileName.Should().Be("osascript");
        command.ArgumentList.TakeLast(2).Should().Equal(iconPath, aliasPath);
        command
            .ArgumentList.SkipLast(2)
            .Should()
            .NotContain(argument => argument.Contains(aliasPath) || argument.Contains(iconPath));
    }
}
