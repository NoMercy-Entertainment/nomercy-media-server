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
using FluentAssertions;
using NoMercy.Setup.Server;

namespace NoMercy.Tests.Setup.Server;

/// <summary>
/// Locks which release asset the self-updater requests per OS and CPU (issue #481).
/// An Apple Silicon Mac must get the arm64 build, and a platform with no published
/// build must yield no asset name rather than a name no release contains.
/// </summary>
public class ReleaseAssetSelectorTests
{
    // Asset names present on release v0.1.533 for each product, taken from the release.
    private static readonly string[] PublishedAssets =
    [
        "NoMercyMediaServer-windows-x64.exe",
        "NoMercyMediaServer-linux-x64",
        "NoMercyMediaServer-macos-x64",
        "NoMercyMediaServer-macos-arm64",
        "NoMercyApp-windows-x64.exe",
        "NoMercyApp-linux-x64",
        "NoMercyApp-macos-x64",
        "NoMercyApp-macos-arm64",
        "NoMercyLauncher-windows-x64.exe",
        "NoMercyLauncher-linux-x64",
        "NoMercyLauncher-macos-x64",
        "NoMercyLauncher-macos-arm64",
        "nomercy-windows-x64.exe",
        "nomercy-linux-x64",
        "nomercy-macos-x64",
        "nomercy-macos-arm64",
    ];

    public static TheoryData<string> Products =>
        ["NoMercyMediaServer", "NoMercyApp", "NoMercyLauncher", "nomercy"];

    [Theory]
    [MemberData(nameof(Products))]
    public void MacOs_Arm64_SelectsArm64Asset(string product)
    {
        string? name = ReleaseAssetSelector.GetAssetName(
            product,
            OSPlatform.OSX,
            Architecture.Arm64
        );

        name.Should().Be($"{product}-macos-arm64");
    }

    [Theory]
    [MemberData(nameof(Products))]
    public void MacOs_X64_SelectsX64Asset(string product)
    {
        string? name = ReleaseAssetSelector.GetAssetName(product, OSPlatform.OSX, Architecture.X64);

        name.Should().Be($"{product}-macos-x64");
    }

    [Theory]
    [MemberData(nameof(Products))]
    public void Linux_Arm64_HasNoPublishedBuild(string product)
    {
        string? name = ReleaseAssetSelector.GetAssetName(
            product,
            OSPlatform.Linux,
            Architecture.Arm64
        );

        name.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(Products))]
    public void Linux_X64_SelectsX64Asset(string product)
    {
        string? name = ReleaseAssetSelector.GetAssetName(
            product,
            OSPlatform.Linux,
            Architecture.X64
        );

        name.Should().Be($"{product}-linux-x64");
    }

    [Theory]
    [MemberData(nameof(Products))]
    public void Windows_SelectsX64Exe(string product)
    {
        string? name = ReleaseAssetSelector.GetAssetName(
            product,
            OSPlatform.Windows,
            Architecture.X64
        );

        name.Should().Be($"{product}-windows-x64.exe");
    }

    [Theory]
    [MemberData(nameof(Products))]
    public void EveryRequestedAsset_IsPublishedOnTheRelease(string product)
    {
        (OSPlatform Os, Architecture Arch)[] platforms =
        [
            (OSPlatform.Windows, Architecture.X64),
            (OSPlatform.Linux, Architecture.X64),
            (OSPlatform.Linux, Architecture.Arm64),
            (OSPlatform.OSX, Architecture.X64),
            (OSPlatform.OSX, Architecture.Arm64),
        ];

        foreach ((OSPlatform os, Architecture arch) in platforms)
        {
            string? name = ReleaseAssetSelector.GetAssetName(product, os, arch);
            if (name is not null)
                PublishedAssets.Should().Contain(name, $"{os}/{arch} must request a real asset");
        }
    }
}
