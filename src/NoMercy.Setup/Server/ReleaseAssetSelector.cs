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

namespace NoMercy.Setup.Server;

/// <summary>
/// Maps an operating system and CPU architecture to the name of the GitHub release
/// asset the self-updater downloads for a product (server, app, launcher, CLI).
/// Pure, so the mapping is unit-testable without the network.
/// </summary>
internal static class ReleaseAssetSelector
{
    internal static string? GetAssetName(string product, OSPlatform os, Architecture architecture)
    {
        if (os == OSPlatform.Windows)
            return $"{product}-windows-x64.exe";

        // No release publishes a linux-arm64 build (the release workflow builds linux-x64
        // only), so asking for one can only miss.
        if (os == OSPlatform.Linux && architecture == Architecture.X64)
            return $"{product}-linux-x64";

        if (os == OSPlatform.OSX && architecture == Architecture.Arm64)
            return $"{product}-macos-arm64";

        if (os == OSPlatform.OSX && architecture == Architecture.X64)
            return $"{product}-macos-x64";

        return null;
    }

    /// <summary>
    /// Log text for a platform that has no asset to request.
    /// </summary>
    internal static string DescribeUnsupported(
        string product,
        OSPlatform os,
        Architecture architecture
    )
    {
        return $"No {product} build is published for {os.ToString().ToLowerInvariant()}-"
            + $"{architecture.ToString().ToLowerInvariant()}.";
    }
}
