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

namespace NoMercy.PluginSdk.Verification;

/// <summary>
/// The SHA-256 of the package a plugin was installed from, kept beside it because
/// the zip is not. It is in the folder's file record, so a later change refuses the load.
/// </summary>
public static class PluginPackageHashMarker
{
    public const string FileName = ".package.sha256";

    private const int HexLength = 64;

    public static void Write(string pluginFolder, string packageHash) =>
        File.WriteAllText(Path.Combine(pluginFolder, FileName), packageHash);

    /// <summary>For a folder whose content no longer came from one package the server hashed.</summary>
    public static void Clear(string pluginFolder)
    {
        string path = Path.Combine(pluginFolder, FileName);

        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>The hash stored beside the given manifest or assembly, or null when none was.</summary>
    public static string? Read(string? pluginFilePath)
    {
        string? folder = pluginFilePath is null
            ? null
            : Path.GetDirectoryName(Path.GetFullPath(pluginFilePath));

        if (folder is null)
            return null;

        string path = Path.Combine(folder, FileName);

        if (!File.Exists(path))
            return null;

        string stored = File.ReadAllText(path).Trim();

        return stored.Length == HexLength && stored.All(Uri.IsHexDigit) ? stored : null;
    }
}
