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
namespace NoMercy.PluginSdk;

/// <summary>
/// Every load of a plugin runs from its own copy of the plugin folder, never
/// from the installed folder itself.
/// <para>
/// The runtime keeps the image it mapped for a path for as long as any load
/// context that used it is alive, and a plugin's context stays alive after
/// Unload until the GC gets to it, which for a plugin with its own threads can
/// be never. Loading the replaced file at the same path then gets the old
/// image: the dashboard reported Torrent Downloader 0.5.0 installed while the
/// process kept running 0.4.1. A fresh folder per load is a path the runtime
/// has never seen, so what is on disk is what runs.
/// </para>
/// <para>
/// A copy is also what lets the installed folder be replaced or deleted on
/// Windows while the old code is still resident: the lock sits on the copy.
/// Copies that could not be deleted while their context was alive are purged
/// at the next start, when nothing is loaded.
/// </para>
/// </summary>
internal static class PluginShadowCopy
{
    internal const string Folder = ".loaded";

    /// <summary>
    /// Copies the folder holding <paramref name="absoluteAssemblyPath"/> into
    /// a new folder under <see cref="Folder"/> and returns the assembly's path
    /// inside that copy.
    /// </summary>
    public static string Create(string pluginsPath, string absoluteAssemblyPath)
    {
        string pluginDir =
            Path.GetDirectoryName(absoluteAssemblyPath)
            ?? throw new InvalidOperationException("Plugin directory could not be determined.");

        string shadowDir = Path.Combine(
            pluginsPath,
            Folder,
            Path.GetFileName(pluginDir),
            Ulid.NewUlid().ToString()
        );

        CopyDirectory(pluginDir, shadowDir);

        return Path.Combine(shadowDir, Path.GetFileName(absoluteAssemblyPath));
    }

    /// <summary>
    /// Removes one copy. Best effort: a copy whose context is still alive keeps
    /// its files locked on Windows, and the next start purges it.
    /// </summary>
    public static void TryDelete(string? shadowDir)
    {
        if (string.IsNullOrEmpty(shadowDir) || !Directory.Exists(shadowDir))
        {
            return;
        }

        try
        {
            Directory.Delete(shadowDir, recursive: true);

            string? parent = Path.GetDirectoryName(shadowDir);
            if (
                parent is not null
                && Directory.Exists(parent)
                && !Directory.EnumerateFileSystemEntries(parent).Any()
            )
            {
                Directory.Delete(parent);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Removes every copy. Only safe when no plugin is loaded, which is true
    /// exactly once: before the boot scan.
    /// </summary>
    public static void PurgeAll(string pluginsPath)
    {
        string root = Path.Combine(pluginsPath, Folder);
        if (!Directory.Exists(root))
        {
            return;
        }

        // One copy at a time, so a copy the OS still holds (Windows keeps a
        // mapped file locked until its context is collected) does not stop the
        // rest from being cleared.
        foreach (string pluginFolder in Directory.EnumerateDirectories(root))
        {
            foreach (string copy in Directory.EnumerateDirectories(pluginFolder))
            {
                TryDelete(copy);
            }
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(root).Any())
            {
                Directory.Delete(root);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string dir in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }
}
