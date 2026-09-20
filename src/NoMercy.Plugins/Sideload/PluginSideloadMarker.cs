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

namespace NoMercy.Plugins.Sideload;

/// <summary>
/// A file beside the plugin saying the owner supplied it rather than a
/// repository.
/// <para>
/// A file rather than a field in the manifest, because the manifest is
/// written by whoever built the plugin and this is a fact about how it got
/// here. Anything a plugin can set about itself is not a fact the server can
/// rely on.
/// </para>
/// </summary>
public static class PluginSideloadMarker
{
    public const string FileName = ".sideloaded";

    private static string? FolderOf(string? manifestPath) =>
        manifestPath is null ? null : Path.GetDirectoryName(Path.GetFullPath(manifestPath));

    public static bool IsMarked(string? manifestPath)
    {
        string? folder = FolderOf(manifestPath);

        return folder is not null && File.Exists(Path.Combine(folder, FileName));
    }

    public static void Mark(string pluginFolder)
    {
        Directory.CreateDirectory(pluginFolder);
        File.WriteAllText(
            Path.Combine(pluginFolder, FileName),
            "This plugin was installed from a file. The server cannot say who wrote it."
        );
    }
}
