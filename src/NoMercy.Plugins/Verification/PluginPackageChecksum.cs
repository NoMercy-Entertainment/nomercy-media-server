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

using System.Security.Cryptography;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugins.Verification;

/// <summary>
/// The one subject a published checksum describes: the .zip a plugin is
/// released as, exactly as it arrived.
/// <para>
/// The archive install hashed the zip and the bare-assembly install hashed the
/// dll, each with its own wording, so the same catalogue entry could pass on
/// one route and fail on the other and neither answer told a publisher which
/// file to hash. Both routes ask this now.
/// </para>
/// </summary>
public static class PluginPackageChecksum
{
    public const string PackageExtension = ".zip";

    public static string Of(string packagePath) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath))).ToLowerInvariant();

    public static async Task<string> OfAsync(string packagePath, CancellationToken ct = default)
    {
        await using FileStream stream = File.OpenRead(packagePath);
        byte[] hash = await SHA256.HashDataAsync(stream, ct);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool IsPackage(string packagePath) =>
        packagePath.EndsWith(PackageExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Checks a package against what was published, and says what went wrong in
    /// the words a publisher can act on. Null means it matched.
    /// </summary>
    public static string? Refuse(string? packagePath, string expectedChecksum)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
            return $"{PluginRefusalCode.ChecksumSubjectMissing}: A checksum was supplied with no "
                + "package to take it over. A checksum is always taken over the plugin's .zip "
                + "package as it was downloaded. Install from the .zip the catalogue names, or "
                + "publish the version without a checksum.";

        string name = Path.GetFileName(packagePath);

        if (!IsPackage(packagePath))
            return $"{PluginRefusalCode.ChecksumSubjectNotAPackage}: The catalogue published a "
                + $"checksum for {name}. A checksum describes the plugin's .zip package, not the "
                + "assembly inside it, so there is nothing here to compare it against. Publish "
                + "the release as a .zip and hash that file, or publish this version without a "
                + "checksum.";

        string actual = Of(packagePath);

        if (string.Equals(actual, expectedChecksum.Trim(), StringComparison.OrdinalIgnoreCase))
            return null;

        return $"{PluginRefusalCode.ChecksumMismatch}: {name} is not the package the catalogue "
            + $"describes. It hashes to {actual} and the catalogue published "
            + $"{expectedChecksum.Trim()}. Download it again, and if it still differs ask the "
            + "publisher to republish the checksum for the file they uploaded.";
    }
}
