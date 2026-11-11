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

using System.Text.RegularExpressions;

namespace NoMercy.Tests.Setup.Server;

public class MacOsMinimumSystemVersionTests
{
    [Theory]
    [InlineData("assets/macos/Info.plist")]
    [InlineData("assets/macos/Info.plist.app")]
    [InlineData("assets/macos/Info.plist.service")]
    public void BundleMinimumMatchesPackageMinimum(string plistPath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (
            directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "packaging/macos/build-pkg.sh"))
        )
            directory = directory.Parent;

        directory.Should().NotBeNull("the test must find the repository root");
        string root = directory!.FullName;
        string packageScript = File.ReadAllText(Path.Combine(root, "packaging/macos/build-pkg.sh"));
        string bundlePlist = File.ReadAllText(Path.Combine(root, plistPath));

        static string MinimumVersion(string contents)
        {
            var match = Regex.Match(
                contents,
                @"<key>LSMinimumSystemVersion</key>\s*<string>([^<]+)</string>",
                RegexOptions.None,
                TimeSpan.FromSeconds(1)
            );
            match.Success.Should().BeTrue("the macOS bundle must declare a minimum version");
            return match.Groups[1].Value;
        }

        MinimumVersion(bundlePlist).Should().Be(MinimumVersion(packageScript));
    }
}
